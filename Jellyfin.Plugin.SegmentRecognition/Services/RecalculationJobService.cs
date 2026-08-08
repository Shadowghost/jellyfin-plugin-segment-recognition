using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Api.Dto;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Orchestrates long-running plugin jobs (recalculate, rematch).
/// Provides:
/// <list type="bullet">
/// <item>per-target mutual exclusion: a recalculation/delete is never run concurrently with itself for the same item or group;</item>
/// <item>cancellation: callers can request a cooperative cancel via <see cref="CancelJob"/>;</item>
/// <item>per-leaf error capture: provider failures are persisted to <see cref="AnalysisStatus.LastError"/> so the UI can surface them;</item>
/// <item>parallelism: leaves run concurrently up to a configurable degree.</item>
/// </list>
/// The registry is in-memory only; jobs do not survive a process restart by design (every action
/// the plugin performs is also written to durable storage).
/// </summary>
public sealed class RecalculationJobService : IDisposable
{
    /// <summary>
    /// Number of completed jobs to keep in the registry for status polling. Older jobs are evicted FIFO.
    /// </summary>
    private const int CompletedJobRetention = 100;

    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILogger<RecalculationJobService> _logger;
    private readonly ConcurrentDictionary<Guid, JobEntry> _jobs = new();

    /// <summary>
    /// Per-target locks, reference-counted so an entry is removed as soon as the last holder lets
    /// go. A plain dictionary that only ever grew retained one semaphore per item id for the
    /// lifetime of the process, which a full-library recalculation turns into tens of thousands.
    /// </summary>
    private readonly Dictionary<string, LockEntry> _locks = new(StringComparer.Ordinal);
    private readonly object _locksSync = new();

    /// <summary>
    /// Guards the find-then-register sequence in <see cref="TrySubmit"/> so two identical requests
    /// arriving together cannot both conclude that no matching job is active.
    /// </summary>
    private readonly object _submitSync = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecalculationJobService"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="logger">The logger.</param>
    public RecalculationJobService(
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILogger<RecalculationJobService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of per-target locks currently retained. Exposed so a test can assert the
    /// registry drains rather than accumulating one entry per item touched.
    /// </summary>
    internal int RetainedLockCount
    {
        get
        {
            lock (_locksSync)
            {
                return _locks.Count;
            }
        }
    }

    /// <summary>
    /// Broadcasts the latest snapshot to every active subscriber of the given job. Drops the
    /// frame on per-subscriber back-pressure (bounded channel) so a slow consumer can't block
    /// the worker thread.
    /// </summary>
    /// <param name="entry">The job entry whose snapshot should be pushed.</param>
    public void Notify(JobEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Subscribers.IsEmpty)
        {
            return;
        }

        var snapshot = entry.ToDto();
        foreach (var writer in entry.Subscribers.Values)
        {
            writer.TryWrite(snapshot);
        }
    }

    /// <summary>
    /// Subscribes to progress updates for the given job. Yields the current snapshot first,
    /// then snapshots produced by each <see cref="Notify"/> call until the job reaches a
    /// terminal status or the caller cancels.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="cancellationToken">A cancellation token (e.g. tied to the websocket).</param>
    /// <returns>An async stream of snapshots.</returns>
    public async IAsyncEnumerable<JobDto> SubscribeAsync(
        Guid jobId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            yield break;
        }

        // Bounded channel with DropOldest so a stalled reader gets a rolling-latest view rather
        // than an unbounded backlog. 16 frames × ~1 KB JSON is plenty of headroom.
        var channel = Channel.CreateBounded<JobDto>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var subId = Guid.NewGuid();
        entry.Subscribers[subId] = channel.Writer;
        try
        {
            // Always emit the current state immediately so a late subscriber doesn't sit in silence.
            channel.Writer.TryWrite(entry.ToDto());

            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var snapshot))
                {
                    yield return snapshot;
                    if (snapshot.Status is "Completed" or "Failed" or "Cancelled")
                    {
                        yield break;
                    }
                }
            }
        }
        finally
        {
            entry.Subscribers.TryRemove(subId, out _);
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Submits a unit of work as a background job and returns the job snapshot immediately. When
    /// an identical job of the same kind is already active, that job is returned instead and no
    /// new work is started; use <see cref="TrySubmit"/> to tell the two cases apart.
    /// </summary>
    /// <param name="kind">Job kind label.</param>
    /// <param name="targetIds">Targets of the job, attached to the snapshot for context.</param>
    /// <param name="totalLeaves">Up-front leaf count for progress display; pass 0 if unknown.</param>
    /// <param name="work">Worker delegate; receives the job entry and a cancellation token.</param>
    /// <param name="skippedIds">Optional identifiers that were dropped before the job started.</param>
    /// <returns>The created job snapshot, or the already-active one.</returns>
    public JobDto Submit(string kind, IReadOnlyList<Guid> targetIds, int totalLeaves, Func<JobEntry, CancellationToken, Task> work, IReadOnlyList<Guid>? skippedIds = null)
    {
        TrySubmit(kind, targetIds, totalLeaves, work, out var dto, skippedIds);
        return dto;
    }

    /// <summary>
    /// Submits a unit of work as a background job unless an identical one is already active.
    /// </summary>
    /// <remarks>
    /// The duplicate check and the registration happen under one lock. Checking with
    /// <see cref="FindActiveJob"/> and then calling <see cref="Submit"/> leaves a window in which
    /// two identical requests both see an empty registry and both start the same work.
    /// </remarks>
    /// <param name="kind">Job kind label.</param>
    /// <param name="targetIds">Targets of the job, attached to the snapshot for context.</param>
    /// <param name="totalLeaves">Up-front leaf count for progress display; pass 0 if unknown.</param>
    /// <param name="work">Worker delegate; receives the job entry and a cancellation token.</param>
    /// <param name="job">The created job, or the already-active job that blocked this submission.</param>
    /// <param name="skippedIds">Optional identifiers that were dropped before the job started.</param>
    /// <returns><c>true</c> if a new job was started; <c>false</c> if an identical one was already active.</returns>
    public bool TrySubmit(
        string kind,
        IReadOnlyList<Guid> targetIds,
        int totalLeaves,
        Func<JobEntry, CancellationToken, Task> work,
        out JobDto job,
        IReadOnlyList<Guid>? skippedIds = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(targetIds);

        JobEntry entry;
        lock (_submitSync)
        {
            if (FindActiveEntry(kind, targetIds) is { } active)
            {
                job = active.ToDto();
                return false;
            }

            entry = new JobEntry(kind, targetIds, totalLeaves);
            if (skippedIds is { Count: > 0 })
            {
                entry.SkippedIds = skippedIds;
            }

            _jobs[entry.JobId] = entry;
            EvictOldCompleted();
        }

        _ = Task.Run(() => RunAsync(entry, work));
        job = entry.ToDto();
        return true;
    }

    /// <summary>
    /// Returns an active (Pending/Running) job of the given kind whose target set exactly matches
    /// <paramref name="targetIds"/>, or null if none is running.
    /// </summary>
    /// <param name="kind">The job kind to match.</param>
    /// <param name="targetIds">The target identifiers to match (order-insensitive).</param>
    /// <returns>The matching active job snapshot, or null.</returns>
    public JobDto? FindActiveJob(string kind, IReadOnlyList<Guid> targetIds)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        return FindActiveEntry(kind, targetIds)?.ToDto();
    }

    /// <summary>
    /// Returns the current snapshot of a job, or null if no such job is in the registry.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>The job snapshot.</returns>
    public JobDto? GetJob(Guid jobId) => _jobs.TryGetValue(jobId, out var entry) ? entry.ToDto() : null;

    /// <summary>
    /// Returns all known jobs newest-first.
    /// </summary>
    /// <returns>Snapshots of every job currently in the registry.</returns>
    public IReadOnlyList<JobDto> ListJobs() =>
        _jobs.Values
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => j.ToDto())
            .ToArray();

    /// <summary>
    /// Requests a cooperative cancel on a job. Returns false if no such job exists.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>True if a cancel was signalled; false otherwise.</returns>
    public bool CancelJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            return false;
        }

        try
        {
            entry.Cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> while holding an exclusive lock on <paramref name="key"/>,
    /// returning <c>false</c> without running it if the lock is already held.
    /// </summary>
    /// <remarks>
    /// The idempotency guard in the controller only rejects a job whose target set matches an
    /// in-flight one exactly, so overlapping-but-different requests (a series and one of its
    /// episodes) both reach analysis for the same item. Without this lock they race on the same
    /// rows: one cleans up while the other inserts, producing lost or duplicated segments.
    /// </remarks>
    /// <param name="key">The lock key (e.g. <c>"item:&lt;guid&gt;"</c>).</param>
    /// <param name="action">The work to run while the lock is held.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> if the action ran; <c>false</c> if the lock was already held.</returns>
    public async Task<bool> RunWithLockAsync(string key, Func<Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        LockEntry entry;
        lock (_locksSync)
        {
            if (!_locks.TryGetValue(key, out entry!))
            {
                entry = new LockEntry();
                _locks[key] = entry;
            }

            // Claimed before the wait so a concurrent releaser cannot evict the entry (and
            // dispose its semaphore) between the lookup here and the wait below.
            entry.Holders++;
        }

        try
        {
            if (!await entry.Semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                await action().ConfigureAwait(false);
                return true;
            }
            finally
            {
                entry.Semaphore.Release();
            }
        }
        finally
        {
            lock (_locksSync)
            {
                if (--entry.Holders == 0 && _locks.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _locks.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Builds the per-item lock key used to serialize analysis of a single library item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The lock key.</returns>
    public static string ItemLockKey(Guid itemId) =>
        string.Create(CultureInfo.InvariantCulture, $"item:{itemId:N}");

    /// <summary>
    /// Persists a captured per-leaf provider error to <see cref="AnalysisStatus.LastError"/>.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="providerName">The provider name.</param>
    /// <param name="containerId">The container (rollup) id for the item, used when inserting a new status row.</param>
    /// <param name="message">The sanitized error message.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RecordProviderErrorAsync(Guid itemId, string providerName, Guid containerId, string message, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.AnalysisStatuses
            .FirstOrDefaultAsync(s => s.ItemId == itemId && s.ProviderName == providerName, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.AnalysisStatuses.Add(new AnalysisStatus
            {
                ItemId = itemId,
                ContainerId = containerId,
                ProviderName = providerName,
                AnalyzedAt = DateTime.UtcNow,
                HasResults = false,
                LastError = message,
                LastErrorAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.LastError = message;
            existing.LastErrorAt = DateTime.UtcNow;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Last writer wins; concurrent leaves running for the same item are fine.
        }
    }

    /// <summary>
    /// Clears any persisted error for a successfully-completed leaf×provider tuple.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="providerName">The provider name.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ClearProviderErrorAsync(Guid itemId, string providerName, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.AnalysisStatuses
            .FirstOrDefaultAsync(s => s.ItemId == itemId && s.ProviderName == providerName, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null || existing.LastError is null)
        {
            return;
        }

        existing.LastError = null;
        existing.LastErrorAt = null;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
        }
    }

    /// <summary>
    /// Resolves the effective parallelism for a job, given an optional override.
    /// </summary>
    /// <param name="overrideValue">Optional override.</param>
    /// <returns>The clamped effective parallelism.</returns>
    public static int ResolveParallelism(int? overrideValue)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var value = overrideValue ?? Math.Max(1, config.MaxParallelGroups);
        return Math.Clamp(value, 1, 8);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Signal before disposing so in-flight workers observe a cancel rather than an
        // ObjectDisposedException from a token they are already waiting on.
        foreach (var entry in _jobs.Values)
        {
            try
            {
                entry.Cts.Cancel();
                entry.Cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already evicted and disposed.
            }
        }

        _jobs.Clear();

        lock (_locksSync)
        {
            foreach (var entry in _locks.Values)
            {
                entry.Semaphore.Dispose();
            }

            _locks.Clear();
        }
    }

    private JobEntry? FindActiveEntry(string kind, IReadOnlyList<Guid> targetIds)
    {
        var wanted = new HashSet<Guid>(targetIds);
        foreach (var entry in _jobs.Values)
        {
            if (entry.Status is not (JobStatus.Pending or JobStatus.Running))
            {
                continue;
            }

            if (!string.Equals(entry.Kind, kind, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.TargetIds.Count == wanted.Count && wanted.SetEquals(entry.TargetIds))
            {
                return entry;
            }
        }

        return null;
    }

    private async Task RunAsync(JobEntry entry, Func<JobEntry, CancellationToken, Task> work)
    {
        // Everything from here on is inside the try: the job's own CancellationTokenSource can be
        // disposed by shutdown or by eviction, and an ObjectDisposedException escaping this method
        // would land on an unobserved Task rather than on the job's status.
        CancellationTokenSource? heartbeatCts = null;
        Task? heartbeat = null;

        try
        {
            entry.Status = JobStatus.Running;
            entry.StartedAt = DateTime.UtcNow;

            // Heartbeat: long analysis phases (ffmpeg, chromaprint cross-match) can go minutes
            // without a progress Notify, and idle WebSocket streams get cut by reverse proxies
            // (nginx ingress defaults to a 60s read timeout). Re-broadcasting the latest snapshot
            // keeps the stream alive; Notify is a no-op when nobody is subscribed.
            heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(entry.Cts.Token);
            var heartbeatToken = heartbeatCts.Token;
            heartbeat = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (await timer.WaitForNextTickAsync(heartbeatToken).ConfigureAwait(false))
                {
                    Notify(entry);
                }
            });

            await work(entry, entry.Cts.Token).ConfigureAwait(false);
            entry.Status = JobStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            entry.Status = JobStatus.Cancelled;
            entry.Message = "Cancelled.";
        }
#pragma warning disable CA1031 // job-level error capture
        catch (Exception ex)
#pragma warning restore CA1031
        {
            entry.Status = JobStatus.Failed;
            entry.Error = SanitizeMessage(ex);
            _logger.LogWarning(ex, "Background job {JobId} ({Kind}) failed", entry.JobId, entry.Kind);
        }
        finally
        {
            if (heartbeatCts is not null)
            {
                await heartbeatCts.CancelAsync().ConfigureAwait(false);
            }

            if (heartbeat is not null)
            {
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Heartbeat ended with the job; expected.
                }
            }

            heartbeatCts?.Dispose();
            entry.CompletedAt = DateTime.UtcNow;
            Notify(entry);
        }
    }

    private void EvictOldCompleted()
    {
        var completed = _jobs.Values
            .Where(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            .OrderByDescending(j => j.CompletedAt ?? j.CreatedAt)
            .ToArray();

        if (completed.Length <= CompletedJobRetention)
        {
            return;
        }

        foreach (var entry in completed.Skip(CompletedJobRetention))
        {
            if (_jobs.TryRemove(entry.JobId, out var removed))
            {
                removed.Cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns the inner exception's message stripped of file paths so admin-visible strings
    /// don't leak server layout. Best-effort; the full stack is always logged.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns>A short, sanitized message.</returns>
    public static string SanitizeMessage(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var msg = ex.Message ?? ex.GetType().Name;
        if (msg.Length > 500)
        {
            msg = string.Concat(msg.AsSpan(0, 500), "…");
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}: {1}", ex.GetType().Name, msg);
    }

    /// <summary>
    /// A per-target lock plus the number of callers currently interested in it.
    /// </summary>
    private sealed class LockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Holders { get; set; }
    }
}
