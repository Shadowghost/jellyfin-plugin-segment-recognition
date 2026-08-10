using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using Jellyfin.Plugin.SegmentRecognition.Api.Dto;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Mutable backing record for a job tracked by <see cref="RecalculationJobService"/>.
/// </summary>
public sealed class JobEntry
{
    private int _completedLeaves;
    private int _failedLeaves;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobEntry"/> class.
    /// </summary>
    /// <param name="kind">The job kind.</param>
    /// <param name="targetIds">The target identifiers.</param>
    /// <param name="totalLeaves">The total leaves to process.</param>
    public JobEntry(string kind, IReadOnlyList<Guid> targetIds, int totalLeaves)
    {
        Kind = kind;
        TargetIds = targetIds;
        TotalLeaves = totalLeaves;
        JobId = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
        Cts = new CancellationTokenSource();
        Counters = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        Subscribers = new ConcurrentDictionary<Guid, ChannelWriter<JobDto>>();
    }

    /// <summary>
    /// Gets the active progress subscribers (websocket sessions). Each subscriber owns a bounded
    /// channel; <see cref="RecalculationJobService.Notify"/> tries to write a snapshot per
    /// subscriber and drops on full to avoid back-pressuring the worker.
    /// </summary>
    internal ConcurrentDictionary<Guid, ChannelWriter<JobDto>> Subscribers { get; }

    /// <summary>
    /// Gets the job identifier.
    /// </summary>
    public Guid JobId { get; }

    /// <summary>
    /// Gets the job kind label.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Gets the target identifiers.
    /// </summary>
    public IReadOnlyList<Guid> TargetIds { get; }

    /// <summary>
    /// Gets the cancellation token source.
    /// </summary>
    public CancellationTokenSource Cts { get; }

    /// <summary>
    /// Gets when the job was created.
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Gets or sets the time work began.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the time work finished.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the current job status.
    /// </summary>
    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>
    /// Gets or sets the total leaves to process.
    /// </summary>
    public int TotalLeaves { get; set; }

    /// <summary>
    /// Gets the number of leaves the worker has finished.
    /// </summary>
    public int CompletedLeaves => Volatile.Read(ref _completedLeaves);

    /// <summary>
    /// Gets the number of leaves that failed.
    /// </summary>
    public int FailedLeaves => Volatile.Read(ref _failedLeaves);

    /// <summary>
    /// Gets the cumulative work counters as a thread-safe bag of named totals. Worker threads
    /// call <see cref="Bump"/> to accumulate; keys are job-kind specific.
    /// </summary>
    public ConcurrentDictionary<string, long> Counters { get; }

    /// <summary>
    /// Gets or sets the requested identifiers that could not be resolved and were dropped before
    /// the job started.
    /// </summary>
    public IReadOnlyList<Guid> SkippedIds { get; set; } = [];

    /// <summary>
    /// Gets or sets a top-level error message.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets a summary message displayed at terminal status.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Increments the completed-leaves counter.
    /// </summary>
    /// <returns>The new value.</returns>
    public int IncrementCompleted() => Interlocked.Increment(ref _completedLeaves);

    /// <summary>
    /// Increments the failed-leaves counter.
    /// </summary>
    /// <returns>The new value.</returns>
    public int IncrementFailed() => Interlocked.Increment(ref _failedLeaves);

    /// <summary>
    /// Atomically adds to a named work counter.
    /// </summary>
    /// <param name="key">The counter key.</param>
    /// <param name="by">The amount to add (default 1).</param>
    public void Bump(string key, long by = 1) => Counters.AddOrUpdate(key, by, (_, current) => current + by);

    /// <summary>
    /// Builds an immutable snapshot DTO.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public JobDto ToDto() => new()
    {
        JobId = JobId,
        Kind = Kind,
        Status = Status.ToString(),
        TargetIds = TargetIds,
        CreatedAt = CreatedAt,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        TotalLeaves = TotalLeaves,
        CompletedLeaves = CompletedLeaves,
        FailedLeaves = FailedLeaves,
        Counters = new Dictionary<string, long>(Counters, StringComparer.Ordinal),
        SkippedIds = SkippedIds,
        Error = Error,
        Message = Message,
    };
}
