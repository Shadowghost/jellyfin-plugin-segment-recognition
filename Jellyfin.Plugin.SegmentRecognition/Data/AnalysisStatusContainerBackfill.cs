using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Data;

/// <summary>
/// One-time background backfill that populates <see cref="Entities.AnalysisStatus.ContainerId"/>
/// for rows written before that column existed (and any row left with <see cref="Guid.Empty"/>),
/// then repairs episode rows stored as their own container (written while the episode's
/// persisted SeriesId was still empty) so they roll up to their series.
/// Runs after <see cref="DatabaseInitializer"/> has applied migrations. Until a row is backfilled
/// it is excluded from the analyzed-items listing, so the listing is briefly incomplete on the
/// first start after upgrade rather than wrong.
/// </summary>
public sealed class AnalysisStatusContainerBackfill : IHostedService, IDisposable
{
    private const int BatchSize = 1000;

    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AnalysisStatusContainerBackfill> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisStatusContainerBackfill"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="logger">The logger.</param>
    public AnalysisStatusContainerBackfill(
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILibraryManager libraryManager,
        ILogger<AnalysisStatusContainerBackfill> logger)
    {
        _dbContextFactory = dbContextFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: never block host startup on the backfill.
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cts.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var totalUpdated = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

                // Exclude degenerate rows (ItemId == Guid.Empty): they can never resolve to a
                // non-empty container, so selecting them would stall progress. Every remaining
                // row has a real ItemId, so GetContainerId always returns a non-empty value and
                // the Empty set strictly shrinks each batch — guaranteeing termination.
                var itemIds = await db.AnalysisStatuses
                    .Where(s => s.ContainerId == Guid.Empty && s.ItemId != Guid.Empty)
                    .Select(s => s.ItemId)
                    .Distinct()
                    .OrderBy(id => id)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (itemIds.Count == 0)
                {
                    break;
                }

                var containerByItem = new Dictionary<Guid, Guid>(itemIds.Count);
                foreach (var id in itemIds)
                {
                    containerByItem[id] = AnalysisGrouping.GetContainerId(_libraryManager.GetItemById(id), id);
                }

                var rows = await db.AnalysisStatuses
                    .Where(s => itemIds.Contains(s.ItemId) && s.ContainerId == Guid.Empty)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Only assign non-empty containers. Tracking how many rows actually advanced lets
                // us guarantee termination: a row that can't be resolved to a non-empty container
                // (e.g. a degenerate Guid.Empty ItemId) would otherwise be re-selected forever.
                var progressed = 0;
                foreach (var row in rows)
                {
                    var containerId = containerByItem[row.ItemId];
                    if (containerId != Guid.Empty)
                    {
                        row.ContainerId = containerId;
                        progressed++;
                    }
                }

                if (progressed == 0)
                {
                    _logger.LogWarning(
                        "ContainerId backfill: {Rows} row(s) could not be resolved to a container and will be skipped.",
                        rows.Count);
                    break;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                totalUpdated += progressed;
                _logger.LogInformation(
                    "ContainerId backfill: resolved {Items} items / {Rows} rows ({Total} total)",
                    itemIds.Count,
                    progressed,
                    totalUpdated);

                // Yield between batches so the backfill never monopolizes the DB on startup.
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested && totalUpdated > 0)
            {
                _logger.LogInformation("ContainerId backfill complete ({Total} rows).", totalUpdated);
            }

            await RepairSelfContainersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
#pragma warning disable CA1031 // a backfill failure must never take down the host
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "ContainerId backfill failed; the analyzed-items listing may be incomplete until the next restart.");
        }
    }

    /// <summary>
    /// Repairs rows whose container is the item itself but whose item now resolves to a
    /// different container (an episode whose series became resolvable, or an alternate version
    /// grouped under a primary). Such rows make the analyzed-items listing show individual
    /// episodes/versions instead of their rollup. Standalone movies legitimately have
    /// ContainerId == ItemId, so each candidate is resolved against the library and only
    /// updated when the recomputed container differs.
    /// </summary>
    private async Task RepairSelfContainersAsync(CancellationToken cancellationToken)
    {
        // One id set up front (GUIDs only, so even very large libraries stay small) instead of
        // re-querying per batch: candidates that turn out to be movies keep ContainerId == ItemId
        // and would otherwise be re-selected forever.
        List<Guid> candidateIds;
        using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            candidateIds = await db.AnalysisStatuses
                .Where(s => s.ContainerId == s.ItemId)
                .Select(s => s.ItemId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var totalRepaired = 0;
        foreach (var batch in candidateIds.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remapped = new Dictionary<Guid, Guid>();
            foreach (var id in batch)
            {
                var containerId = AnalysisGrouping.GetContainerId(_libraryManager.GetItemById(id), id);
                if (containerId != id)
                {
                    remapped[id] = containerId;
                }
            }

            if (remapped.Count == 0)
            {
                continue;
            }

            using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var remappedIds = remapped.Keys.ToList();
            var rows = await db.AnalysisStatuses
                .Where(s => remappedIds.Contains(s.ItemId) && s.ContainerId == s.ItemId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                row.ContainerId = remapped[row.ItemId];
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            totalRepaired += rows.Count;

            // Yield between batches so the repair never monopolizes the DB on startup.
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        if (totalRepaired > 0)
        {
            _logger.LogInformation("ContainerId repair: re-homed {Total} row(s) to their container.", totalRepaired);
        }
    }
}
