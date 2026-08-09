using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;

/// <summary>
/// Scheduled task that runs all analysis pipelines (chapter names, black frames, chromaprint)
/// and pushes results into Jellyfin's segment store.
/// </summary>
public class AnalyzeSegmentsTask : IScheduledTask
{
    /// <summary>
    /// Fraction of known items that may disappear from the library in one run before the orphan
    /// sweep treats the result as an incomplete library read and refuses to delete anything.
    /// </summary>
    private const double MaxOrphanFraction = 0.5;

    /// <summary>
    /// Below this many known items the orphan-ratio check is skipped: on a tiny cache a single
    /// genuine removal can trivially exceed any percentage threshold.
    /// </summary>
    private const int MinItemsForOrphanRatioCheck = 20;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ChapterNameProvider _chapterNameProvider;
    private readonly BlackFrameProvider _blackFrameProvider;
    private readonly ChromaprintProvider _chromaprintProvider;
    private readonly ILogger<AnalyzeSegmentsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyzeSegmentsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSegmentManager">The media segment manager.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="chapterNameProvider">The chapter name provider.</param>
    /// <param name="blackFrameProvider">The black frame provider.</param>
    /// <param name="chromaprintProvider">The chromaprint provider.</param>
    /// <param name="logger">The logger.</param>
    public AnalyzeSegmentsTask(
        ILibraryManager libraryManager,
        IMediaSegmentManager mediaSegmentManager,
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ChapterNameProvider chapterNameProvider,
        BlackFrameProvider blackFrameProvider,
        ChromaprintProvider chromaprintProvider,
        ILogger<AnalyzeSegmentsTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _dbContextFactory = dbContextFactory;
        _chapterNameProvider = chapterNameProvider;
        _blackFrameProvider = blackFrameProvider;
        _chromaprintProvider = chromaprintProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Analyze Segments";

    /// <inheritdoc />
    public string Key => "SegmentRecognitionAnalyze";

    /// <inheritdoc />
    public string Description => "Runs chapter name, black frame, and chromaprint analysis on all supported media items based on library options, " +
        "then pushes discovered segments into Jellyfin's segment store.";

    /// <inheritdoc />
    public string Category => "Segment Recognition";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        _logger.LogInformation("Segment analysis task starting");

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var forceOverwrite = config.ForceRegenerate;

        _logger.LogInformation(
            "Configuration: ChapterName={ChapterEnabled}, BlackFrame={BlackFrameEnabled}, Chromaprint={ChromaprintEnabled}, ForceRegenerate={Force}",
            config.EnableChapterNameProvider,
            config.EnableBlackFrameProvider,
            config.EnableChromaprintProvider,
            forceOverwrite);

        if (forceOverwrite)
        {
            _logger.LogInformation("ForceRegenerate is enabled - all segments will be re-pushed to Jellyfin after analysis");
        }

        var reanalyzeBlackFrames = config.ReanalyzeBlackFrames;
        if (reanalyzeBlackFrames)
        {
            _logger.LogInformation("ReanalyzeBlackFrames is enabled - clearing all cached black frame data");

            using var cleanupDb = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await cleanupDb.BlackFrameResults.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await cleanupDb.CropDetectResults.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await cleanupDb.ChapterAnalysisResults
                .Where(r => SegmentSourceNames.BlackFrameOwned.Contains(r.MatchedChapterName))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await cleanupDb.AnalysisStatuses
                .Where(s => s.ProviderName == ProviderNames.BlackFrame)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await PruneOrphanedDataAsync(cancellationToken).ConfigureAwait(false);

        progress.Report(0);

        // Enumerate seasons (with their episodes) and movies upfront so progress can be
        // reported at item granularity even though work is dispatched per season.
        // One bulk query for every episode in the library, then in-memory grouping by
        // SeasonId — was N+1 queries (1 for the season list, 1 per season for episodes),
        // which scales to thousands of round-trips on a large library and blocks the
        // task before any item gets enqueued.
        var seasonEpisodes = GetEpisodesGroupedBySeason(cancellationToken);

        var movies = GetMovieList();
        var totalItems = seasonEpisodes.Values.Sum(e => e.Count) + movies.Count;

        _logger.LogInformation(
            "Found {Seasons} seasons ({Episodes} episodes) and {Movies} movies in library",
            seasonEpisodes.Count,
            totalItems - movies.Count,
            movies.Count);

        if (totalItems == 0)
        {
            _logger.LogInformation("No items to process, task complete");
            progress.Report(100);
            return;
        }

        var stats = new TaskStats();
        var processed = 0;

        // Load per-item staleness for the entire library in one batched pass (chunked internally),
        // so the per-season/per-movie hot paths do pure in-memory lookups instead of re-querying
        // the DB once per season and once per item.
        var staleness = await LoadStalenessAsync(
            seasonEpisodes.Values.SelectMany(e => e).Concat(movies).ToList(),
            config,
            cancellationToken).ConfigureAwait(false);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(config.MaxParallelGroups, Environment.ProcessorCount)),
            CancellationToken = cancellationToken
        };

        // Process each season end-to-end: analyze every episode (chapters + blackframe + fingerprints),
        // run the per-season chromaprint comparison if the season is dirty, then push.
        await Parallel.ForEachAsync(seasonEpisodes, parallelOptions, async (entry, ct) =>
        {
            await ProcessSeasonAsync(entry.Key, entry.Value, config, forceOverwrite, staleness, stats, ct).ConfigureAwait(false);

            var current = Interlocked.Add(ref processed, entry.Value.Count);
            progress.Report(Math.Min(99.0, 100.0 * current / totalItems));

            _logger.LogDebug(
                "Item progress: {Done}/{Total} ({ChapterNew} chapter, {BlackFrameNew} black frame, {Fingerprints} fingerprints, {Pushed} pushed, {Skipped} skipped)",
                current,
                totalItems,
                stats.ChapterAnalyzed,
                stats.BlackFrameAnalyzed,
                stats.FingerprintsGenerated,
                stats.Pushed,
                stats.AnalysisSkipped);
        }).ConfigureAwait(false);

        // Movies: no grouping, no comparison - analyze and push each independently.
        await Parallel.ForEachAsync(movies, parallelOptions, async (movie, ct) =>
        {
            await ProcessMovieAsync(movie, config, forceOverwrite, staleness, stats, ct).ConfigureAwait(false);

            var current = Interlocked.Increment(ref processed);
            progress.Report(Math.Min(99.0, 100.0 * current / totalItems));
        }).ConfigureAwait(false);

        // If ForceRegenerate was set, re-push ALL items with cached results (not just newly analyzed ones)
        if (forceOverwrite)
        {
            await ForcePushAllSegmentsAsync(stats.PushedItemIds, progress, cancellationToken).ConfigureAwait(false);
        }

        // Clear the one-shot flags only now that the work they requested has actually been done.
        // Clearing them up front lost the request whenever the task was cancelled or crashed -
        // and in ReanalyzeBlackFrames' case it did so *after* already dropping the cache, leaving
        // the user with neither the old data nor the re-analysis they asked for.
        if (forceOverwrite || reanalyzeBlackFrames)
        {
            var liveConfig = Plugin.Instance?.Configuration;
            if (liveConfig is not null)
            {
                liveConfig.ForceRegenerate = false;
                liveConfig.ReanalyzeBlackFrames = false;
                Plugin.Instance?.SaveConfiguration();
            }
        }

        progress.Report(100);

        if (stats.AnalysisFailed > 0)
        {
            _logger.LogWarning(
                "Segment analysis completed with {FailedCount} failures out of {TotalAttempts} analysis operations",
                stats.AnalysisFailed,
                stats.TotalWork + stats.AnalysisFailed);
        }

        _logger.LogInformation(
            "Segment analysis task complete: {ChapterAnalyzed} chapter, {BlackFrameAnalyzed} black frame, "
            + "{FingerprintsGenerated} fingerprints, {SeasonsAnalyzed} seasons compared, "
            + "{Pushed} items pushed, {PushSkipped} push skipped, {Failed} failed",
            stats.ChapterAnalyzed,
            stats.BlackFrameAnalyzed,
            stats.FingerprintsGenerated,
            stats.SeasonsAnalyzed,
            stats.Pushed,
            stats.PushSkipped,
            stats.AnalysisFailed);
    }

    /// <summary>
    /// Removes cached rows whose owning <c>ItemId</c> no longer exists in Jellyfin's library.
    /// <para>
    /// Jellyfin re-derives <see cref="BaseItem.Id"/> from the item's path and library options.
    /// File renames, library re-adds, library path changes, and series matching to a different
    /// provider ID all silently mint a new GUID without firing <c>ItemRemoved</c>.
    /// </para>
    /// </summary>
    private async Task PruneOrphanedDataAsync(CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var allIds = new HashSet<Guid>();
        allIds.UnionWith(await db.ChromaprintResults.AsNoTracking().Select(r => r.ItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        allIds.UnionWith(await db.ChapterAnalysisResults.AsNoTracking().Select(r => r.ItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        allIds.UnionWith(await db.AnalysisStatuses.AsNoTracking().Select(s => s.ItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        allIds.UnionWith(await db.BlackFrameResults.AsNoTracking().Select(r => r.ItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        allIds.UnionWith(await db.CropDetectResults.AsNoTracking().Select(r => r.ItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();

        // One DB query for every Guid in BaseItems, then in-memory set subtraction.
        // Avoids N per-item GetItemById lookups that fall through to the DB on a cold
        // cache after a fresh restart and turn the orphan sweep into minutes of upfront
        // blocking before any item is enqueued.
        var validIds = _libraryManager.GetItemIds(new InternalItemsQuery { IncludeOwnedItems = true }).ToHashSet();

        var knownIds = allIds.Count;
        allIds.ExceptWith(validIds);

        if (IsOrphanSweepUnsafe(knownIds, validIds.Count, allIds.Count, out var reason))
        {
            _logger.LogWarning(
                "Orphan sweep: refusing to delete {Orphans} of {Known} cached item(s) - {Reason}. "
                + "Re-run the task once the library has finished scanning.",
                allIds.Count,
                knownIds,
                reason);
            return;
        }

        if (allIds.Count == 0)
        {
            _logger.LogDebug("Orphan sweep: no orphan rows found");
            return;
        }

        var orphans = allIds.ToList();

        const int chunkSize = 500;
        var totalDeleted = 0;
        for (var i = 0; i < orphans.Count; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = orphans.Skip(i).Take(chunkSize).ToList();
            totalDeleted += await db.ChromaprintResults.Where(r => chunk.Contains(r.ItemId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            totalDeleted += await db.ChapterAnalysisResults.Where(r => chunk.Contains(r.ItemId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            totalDeleted += await db.AnalysisStatuses.Where(s => chunk.Contains(s.ItemId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            totalDeleted += await db.BlackFrameResults.Where(r => chunk.Contains(r.ItemId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            totalDeleted += await db.CropDetectResults.Where(r => chunk.Contains(r.ItemId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Orphan sweep: deleted {Rows} row(s) across {Items} item(s) whose ItemId is no longer in the library",
            totalDeleted,
            orphans.Count);
    }

    /// <summary>
    /// Decides whether an orphan sweep result is trustworthy enough to delete on.
    /// </summary>
    /// <remarks>
    /// The sweep deletes every cached row whose item is absent from a single library query, so a
    /// query that comes back empty or truncated - library database locked, a scan in flight, a
    /// library temporarily unmounted - would wipe the whole cache and force hours of re-analysis.
    /// A genuine mass-removal is recoverable (just delete the plugin database); an accidental wipe
    /// is not, so the check errs towards keeping stale rows.
    /// </remarks>
    /// <param name="knownItemCount">Distinct item ids present in the plugin database.</param>
    /// <param name="libraryItemCount">Item ids the library returned.</param>
    /// <param name="orphanCount">Known ids absent from the library result.</param>
    /// <param name="reason">Human-readable reason when unsafe.</param>
    /// <returns><c>true</c> when the sweep must be skipped.</returns>
    internal static bool IsOrphanSweepUnsafe(
        int knownItemCount,
        int libraryItemCount,
        int orphanCount,
        out string reason)
    {
        if (orphanCount == 0)
        {
            reason = string.Empty;
            return false;
        }

        if (libraryItemCount == 0)
        {
            reason = "the library returned zero items";
            return true;
        }

        if (knownItemCount >= MinItemsForOrphanRatioCheck
            && orphanCount >= knownItemCount * MaxOrphanFraction)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"{orphanCount / (double)knownItemCount:P0} of the cache would be deleted, which looks like an incomplete library read");
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Runs all analysis pipelines for one season's episodes, then (if needed) the chromaprint
    /// group comparison, then pushes segments for every episode in the season.
    /// </summary>
    private async Task ProcessSeasonAsync(
        Guid seasonId,
        IReadOnlyList<BaseItem> episodes,
        PluginConfiguration config,
        bool forceOverwrite,
        StalenessSnapshot staleness,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        if (episodes.Count == 0)
        {
            return;
        }

        var seasonLabel = episodes[0] is Episode firstEp
            ? $"\"{firstEp.SeriesName}\" - \"{firstEp.SeasonName}\""
            : seasonId.ToString();

        _logger.LogDebug("Processing season {Label} ({Count} episodes)", seasonLabel, episodes.Count);

        // Every episode in a season shares the same library, so resolve its options once instead
        // of once per episode in each loop below.
        var libraryOptions = _libraryManager.GetLibraryOptions(episodes[0]);

        // 1) Analyze each episode (chapters + blackframe + chromaprint fingerprint generation).
        var anyNewWork = false;
        foreach (var ep in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await AnalyzeItemAsync(ep, libraryOptions, config, staleness, stats, cancellationToken).ConfigureAwait(false))
            {
                anyNewWork = true;
            }
        }

        // 2) Chromaprint per-season comparison. Only run if at least one episode is fingerprintable
        //    AND either we just generated new fingerprints OR previously stored results are stale.
        var ranComparison = false;
        var hasFingerprintableItems = episodes.Any(e => ChromaprintProvider.GetGroupId(e) != Guid.Empty);
        if (config.EnableChromaprintProvider && hasFingerprintableItems)
        {
            var needsCompare = anyNewWork || await IsSeasonStaleAsync(seasonId, config, cancellationToken).ConfigureAwait(false);
            if (needsCompare)
            {
                _logger.LogDebug("Comparing fingerprints for {Label} ({Count} episodes)", seasonLabel, episodes.Count);
                try
                {
                    await _chromaprintProvider.AnalyzeGroupAsync(seasonId, cancellationToken).ConfigureAwait(false);
                    stats.IncrementSeasonsAnalyzed();
                    ranComparison = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Chromaprint group analysis failed for {Label} ({SeasonId})", seasonLabel, seasonId);
                }
            }

            if (ranComparison
                && await RetryWiderIntrosAsync(seasonId, seasonLabel, stats, cancellationToken).ConfigureAwait(false))
            {
                anyNewWork = true;
            }
        }

        // 3) Push segments. Comparison may produce results for episodes that had no new work
        //    themselves, so push the whole season whenever we did any new work or ran comparison.
        if (anyNewWork || ranComparison)
        {
            _logger.LogDebug("Pushing segments for {Label}", seasonLabel);
            foreach (var ep in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PushSegmentsAsync(ep, libraryOptions, forceOverwrite, stats, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessMovieAsync(
        BaseItem movie,
        PluginConfiguration config,
        bool forceOverwrite,
        StalenessSnapshot staleness,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        var libraryOptions = _libraryManager.GetLibraryOptions(movie);
        if (await AnalyzeItemAsync(movie, libraryOptions, config, staleness, stats, cancellationToken).ConfigureAwait(false))
        {
            await PushSegmentsAsync(movie, libraryOptions, forceOverwrite, stats, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Determines whether a season's chromaprint comparison output is out-of-date with respect
    /// to the current configuration, or whether a previous run was interrupted between
    /// fingerprint generation and group comparison.
    /// </summary>
    private async Task<bool> IsSeasonStaleAsync(
        Guid seasonId,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var comparisonHash = ConfigHasher.ChromaprintComparison(config);

        // Pending: a fingerprint exists for this season but the item has no Chromaprint
        // analysis status row - the prior run was cancelled after fingerprinting but before
        // group comparison.
        var hasPending = await db.ChromaprintResults
            .AsNoTracking()
            .AnyAsync(
                c => c.SeasonId == seasonId
                    && c.AnalysisDurationSeconds > 0
                    && !db.AnalysisStatuses.Any(s => s.ItemId == c.ItemId && s.ProviderName == ProviderNames.Chromaprint),
                cancellationToken)
            .ConfigureAwait(false);
        if (hasPending)
        {
            return true;
        }

        // Stale: existing chromaprint segment results were produced under a different config.
        var hasStaleResults = await db.ChapterAnalysisResults
            .AsNoTracking()
            .Where(r => (r.MatchedChapterName == SegmentSourceNames.ChromaprintIntro
                    || r.MatchedChapterName == SegmentSourceNames.ChromaprintCredits)
                && r.ConfigHash != comparisonHash)
            .Join(
                db.ChromaprintResults.Where(c => c.SeasonId == seasonId),
                r => r.ItemId,
                c => c.ItemId,
                (r, c) => r.ItemId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (hasStaleResults)
        {
            return true;
        }

        // Zero-match status with old/missing config hash: previous run found no matches under
        // different settings - re-run so the new config gets a chance.
        var hasZeroMatchStale = await db.AnalysisStatuses
            .AsNoTracking()
            .Where(s => s.ProviderName == ProviderNames.Chromaprint
                && !s.HasResults
                && (s.ConfigHash == null || s.ConfigHash != comparisonHash))
            .Join(
                db.ChromaprintResults.Where(c => c.SeasonId == seasonId),
                s => s.ItemId,
                c => c.ItemId,
                (s, c) => s.ItemId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        return hasZeroMatchStale;
    }

    private async Task<StalenessSnapshot> LoadStalenessAsync(
        IReadOnlyCollection<BaseItem> items,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var itemIds = items.Select(i => i.Id).Distinct().ToList();
        if (itemIds.Count == 0)
        {
            return new StalenessSnapshot();
        }

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var chapterHash = ConfigHasher.ChapterName(config);
        var bfExtractionHash = ConfigHasher.BlackFrameExtraction(config);
        var bfSegmentHash = ConfigHasher.BlackFrameSegments(config);

        var chapterAnalyzed = new HashSet<Guid>();
        var blackFrameAnalyzed = new HashSet<Guid>();
        var chromaprintAnalyzed = new HashSet<Guid>();
        var staleChapter = new HashSet<Guid>();
        var staleBlackFrameExtraction = new HashSet<Guid>();
        var staleBlackFrameSegments = new HashSet<Guid>();
        var withFingerprint = new HashSet<Guid>();
        var fingerprintHashes = new Dictionary<(Guid, string), string?>();
        var fingerprintRegions = new Dictionary<(Guid, string), int>();

        // Chunk by SQLite's parameter cap so a 1000-episode "season" (or a forced movie batch)
        // doesn't blow the IN(...) limit when EF expands Contains().
        foreach (var chunk in itemIds.Chunk(500))
        {
            // Staleness is read off the STATUS row, not off the result rows. An item that matched
            // nothing has no result rows to carry a config hash, so a result-row-only check could
            // never re-analyze exactly the items a widened keyword list is meant to catch.
            var statuses = await db.AnalysisStatuses
                .AsNoTracking()
                .Where(s => chunk.Contains(s.ItemId))
                .Select(s => new { s.ItemId, s.ProviderName, s.ConfigHash })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var s in statuses)
            {
                if (s.ProviderName == ProviderNames.ChapterName)
                {
                    chapterAnalyzed.Add(s.ItemId);
                    if (!string.Equals(s.ConfigHash, chapterHash, StringComparison.Ordinal))
                    {
                        staleChapter.Add(s.ItemId);
                    }
                }
                else if (s.ProviderName == ProviderNames.BlackFrame)
                {
                    blackFrameAnalyzed.Add(s.ItemId);
                    if (!string.Equals(s.ConfigHash, bfSegmentHash, StringComparison.Ordinal))
                    {
                        staleBlackFrameSegments.Add(s.ItemId);
                    }
                }
                else if (s.ProviderName == ProviderNames.Chromaprint)
                {
                    chromaprintAnalyzed.Add(s.ItemId);
                }
            }

            // Result rows written by an older build carry a hash on the row but not on the status.
            // Treat a stale row as stale too, so upgrades converge instead of getting stuck.
            var staleChapterIds = await db.ChapterAnalysisResults
                .AsNoTracking()
                .Where(r => chunk.Contains(r.ItemId)
                    && !SegmentSourceNames.ForeignToChapterName.Contains(r.MatchedChapterName)
                    && r.ConfigHash != chapterHash)
                .Select(r => r.ItemId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            staleChapter.UnionWith(staleChapterIds);

            // Extraction staleness means the cached samples themselves are unusable, so the
            // (expensive) ffmpeg scan has to re-run. Segment staleness only means the cheap half
            // of the pipeline changed and can be replayed from cache.
            var staleBfIds = await db.BlackFrameResults
                .AsNoTracking()
                .Where(r => chunk.Contains(r.ItemId) && r.ConfigHash != bfExtractionHash)
                .Select(r => r.ItemId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            staleBlackFrameExtraction.UnionWith(staleBfIds);

            var fingerprintRows = await db.ChromaprintResults
                .AsNoTracking()
                .Where(r => chunk.Contains(r.ItemId))
                .Select(r => new { r.ItemId, r.Region, r.ConfigHash, r.AnalysisDurationSeconds })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var r in fingerprintRows)
            {
                withFingerprint.Add(r.ItemId);
                fingerprintHashes[(r.ItemId, r.Region)] = r.ConfigHash;
                fingerprintRegions[(r.ItemId, r.Region)] = r.AnalysisDurationSeconds;
            }
        }

        // A full re-analysis subsumes a segment rebuild; don't do both.
        staleBlackFrameSegments.ExceptWith(staleBlackFrameExtraction);

        return new StalenessSnapshot
        {
            ChapterAnalyzed = chapterAnalyzed,
            BlackFrameAnalyzed = blackFrameAnalyzed,
            ChromaprintAnalyzed = chromaprintAnalyzed,
            StaleChapterItems = staleChapter,
            StaleBlackFrameItems = staleBlackFrameExtraction,
            RebuildBlackFrameItems = staleBlackFrameSegments,
            ItemsWithFingerprint = withFingerprint,
            FingerprintHashes = fingerprintHashes,
            FingerprintRegions = fingerprintRegions,
        };
    }

    /// <summary>
    /// Runs all applicable analysis pipelines for a single item.
    /// Returns <c>true</c> if any new analysis work was performed (meaning segments should be pushed).
    /// </summary>
    private async Task<bool> AnalyzeItemAsync(
        BaseItem item,
        MediaBrowser.Model.Configuration.LibraryOptions libraryOptions,
        PluginConfiguration config,
        StalenessSnapshot staleness,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        var snapshotBefore = stats.TotalWork;
        var disabledProviders = libraryOptions.DisabledMediaSegmentProviders;

        _logger.LogDebug(
            "Item \"{ItemName}\": DisabledMediaSegmentProviders=[{Disabled}]",
            item.Name,
            string.Join(", ", disabledProviders));

        // Chapter name analysis
        if (config.EnableChapterNameProvider && !IsProviderDisabled(disabledProviders, ProviderNames.ChapterName))
        {
            var needsChapterAnalysis = !staleness.ChapterAnalyzed.Contains(item.Id);

            if (!needsChapterAnalysis && staleness.StaleChapterItems.Contains(item.Id))
            {
                _logger.LogDebug("Chapter config changed for \"{ItemName}\", re-analyzing", item.Name);
                await _chapterNameProvider.CleanupExtractedData(item.Id, cancellationToken).ConfigureAwait(false);
                needsChapterAnalysis = true;
            }

            if (needsChapterAnalysis)
            {
                _logger.LogDebug("Analyzing chapters for \"{ItemName}\" ({Path})", item.Name, item.Path);
                try
                {
                    await _chapterNameProvider.AnalyzeAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    stats.IncrementChapterAnalyzed();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Chapter analysis failed for \"{ItemName}\" ({Path})", item.Name, item.Path);
                    stats.IncrementAnalysisFailed();
                }
            }
        }

        // Black frame analysis
        if (config.EnableBlackFrameProvider && !IsProviderDisabled(disabledProviders, ProviderNames.BlackFrame))
        {
            var needsBlackFrame = !staleness.BlackFrameAnalyzed.Contains(item.Id);

            if (!needsBlackFrame && staleness.StaleBlackFrameItems.Contains(item.Id))
            {
                _logger.LogDebug("BlackFrame config changed for \"{ItemName}\", re-analyzing", item.Name);
                await _blackFrameProvider.CleanupExtractedData(item.Id, cancellationToken).ConfigureAwait(false);
                needsBlackFrame = true;
            }

            if (needsBlackFrame)
            {
                _logger.LogDebug("Analyzing black frames for \"{ItemName}\" ({Path})", item.Name, item.Path);
                try
                {
                    await _blackFrameProvider.AnalyzeAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    stats.IncrementBlackFrameAnalyzed();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Black frame analysis failed for \"{ItemName}\" ({Path})", item.Name, item.Path);
                    stats.IncrementAnalysisFailed();
                }
            }
            else if (staleness.RebuildBlackFrameItems.Contains(item.Id))
            {
                // Only the cheap half of the pipeline changed (clustering thresholds, duration
                // windows, refinement settings). Replay it from the cached samples instead of
                // paying for another full-file ffmpeg scan.
                _logger.LogDebug("BlackFrame segment config changed for \"{ItemName}\", rebuilding from cache", item.Name);
                try
                {
                    await _blackFrameProvider.RebuildSegmentsFromCacheAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    stats.IncrementBlackFrameAnalyzed();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Black frame segment rebuild failed for \"{ItemName}\" ({Path})", item.Name, item.Path);
                    stats.IncrementAnalysisFailed();
                }
            }
        }

        // Chromaprint fingerprinting (generation only - comparison is done per-group later)
        if (config.EnableChromaprintProvider && !IsProviderDisabled(disabledProviders, ProviderNames.Chromaprint)
            && ChromaprintProvider.GetGroupId(item) != Guid.Empty)
        {
            await EnsureChromaprintFingerprintAsync(item, SegmentSourceNames.RegionIntro, config, staleness, stats, cancellationToken).ConfigureAwait(false);

            if (config.EnableCreditsFingerprinting)
            {
                await EnsureChromaprintFingerprintAsync(item, SegmentSourceNames.RegionCredits, config, staleness, stats, cancellationToken).ConfigureAwait(false);
            }
        }

        var didWork = stats.TotalWork != snapshotBefore;
        if (!didWork)
        {
            stats.IncrementAnalysisSkipped();
        }

        return didWork;
    }

    private async Task EnsureChromaprintFingerprintAsync(
        BaseItem item,
        string region,
        PluginConfiguration config,
        StalenessSnapshot staleness,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        var expectedHash = string.Equals(region, SegmentSourceNames.RegionCredits, StringComparison.Ordinal)
            ? ConfigHasher.ChromaprintCredits(config)
            : ConfigHasher.ChromaprintIntro(config);

        // If the item has a Chromaprint AnalysisStatus but no fingerprint rows, it was marked as
        // already analyzed by an external source (e.g. the intro-skipper import task). Don't
        // fingerprint it - there's no signal to compare against and re-analysis would defeat the
        // purpose of the import. Items with at least one fingerprint row fall through to the
        // existing ConfigHash-based regen logic below.
        if (!staleness.ItemsWithFingerprint.Contains(item.Id)
            && staleness.ChromaprintAnalyzed.Contains(item.Id))
        {
            return;
        }

        // In-memory check against the staleness snapshot - no DB round-trip in the common case
        // where the fingerprint already exists and is still usable.
        if (staleness.FingerprintHashes.TryGetValue((item.Id, region), out var existingHash))
        {
            // Two independent reasons to regenerate. The hash covers settings that change the
            // fingerprint's bytes; the region covers how much of the item it spans. Keeping them
            // apart is what lets a fingerprint that is already wide enough survive - a stored
            // region is only stale when the wanted one is *wider*, never when it is narrower.
            var wantedRegion = WantedRegionSeconds(item, region);
            var storedRegion = staleness.FingerprintRegions.TryGetValue((item.Id, region), out var stored)
                ? stored
                : 0;

            if (string.Equals(existingHash, expectedHash, StringComparison.Ordinal)
                && wantedRegion <= storedRegion + 1)
            {
                return;
            }

            _logger.LogDebug(
                "Chromaprint {Region} fingerprint for \"{ItemName}\" is stale (covers {Stored}s, wants {Wanted:F0}s), regenerating",
                region,
                item.Name,
                storedRegion,
                wantedRegion);

            // Config changed: drop the stale row so GenerateFingerprintAsync (which skips items
            // that already have a row) will regenerate it. This is the only path that needs a DB
            // context here.
            using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.ChromaprintResults
                .Where(r => r.ItemId == item.Id && r.Region == region)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogDebug("Generating chromaprint {Region} fingerprint for \"{ItemName}\" ({Path})", region, item.Name, item.Path);
        try
        {
            await _chromaprintProvider.GenerateFingerprintAsync(item.Id, region, cancellationToken).ConfigureAwait(false);
            stats.IncrementFingerprintsGenerated();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chromaprint {Region} fingerprinting failed for \"{ItemName}\" ({Path})", region, item.Name, item.Path);
            stats.IncrementAnalysisFailed();
        }
    }

    /// <summary>
    /// Re-fingerprints a season's unconvincing intros over a wider region and compares again.
    /// </summary>
    /// <remarks>
    /// The first-pass region covers 99% of intros; the rest are unreachable at any threshold that
    /// stays cheap for the other 99%, since long-form drama can run fifteen minutes of cold open
    /// before its titles. So widen only for the items a season says are wrong.
    /// </remarks>
    /// <returns><c>true</c> if anything was re-fingerprinted.</returns>
    private async Task<bool> RetryWiderIntrosAsync(
        Guid seasonId,
        string seasonLabel,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        var candidates = await _chromaprintProvider
            .GetIntroRetryCandidatesAsync(seasonId, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return false;
        }

        _logger.LogDebug(
            "Retrying {Count} intro fingerprint(s) over a wider region for {Label}",
            candidates.Count,
            seasonLabel);

        var regenerated = 0;
        foreach (var (itemId, regionSeconds) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
                {
                    await db.ChromaprintResults
                        .Where(r => r.ItemId == itemId && r.Region == SegmentSourceNames.RegionIntro)
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                await _chromaprintProvider.GenerateFingerprintAsync(
                    itemId, SegmentSourceNames.RegionIntro, cancellationToken, regionSeconds).ConfigureAwait(false);

                stats.IncrementFingerprintsGenerated();
                regenerated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Wider intro fingerprinting failed for item {ItemId}", itemId);
                stats.IncrementAnalysisFailed();
            }
        }

        if (regenerated == 0)
        {
            return false;
        }

        try
        {
            await _chromaprintProvider.AnalyzeGroupAsync(seasonId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chromaprint re-comparison failed for {Label} ({SeasonId})", seasonLabel, seasonId);
        }

        return true;
    }

    /// <summary>
    /// How many seconds of an item this run wants fingerprinted for a region.
    /// </summary>
    /// <remarks>
    /// Only the intro region is answered here. The credits region is anchored to the end of the
    /// audio, whose length needs an ffprobe call to know, so its staleness stays on the config
    /// hash - which still carries the credits duration.
    /// </remarks>
    private static double WantedRegionSeconds(BaseItem item, string region)
    {
        if (string.Equals(region, SegmentSourceNames.RegionCredits, StringComparison.Ordinal))
        {
            return 0;
        }

        var runtimeTicks = item.RunTimeTicks ?? 0;
        return runtimeTicks <= 0
            ? 0
            : ChromaprintRegions.FirstPass(runtimeTicks / (double)TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// Hands one item to Jellyfin's segment providers.
    /// </summary>
    /// <param name="item">The item to push.</param>
    /// <param name="libraryOptions">The owning library's options.</param>
    /// <param name="forceOverwrite">Whether to replace segments Jellyfin already holds.</param>
    /// <param name="stats">Run statistics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// The gate is "ever analyzed", not "currently has results". Jellyfin drops a segment only
    /// when the provider is asked again and returns nothing, so an item that lost its results is
    /// exactly the one that must be pushed - gating on results left stale segments no later run
    /// could clear. Items never analyzed are still skipped; nothing was ever given to Jellyfin.
    /// </remarks>
    private async Task PushSegmentsAsync(
        BaseItem item,
        MediaBrowser.Model.Configuration.LibraryOptions libraryOptions,
        bool forceOverwrite,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var analyzed = await db.AnalysisStatuses
            .AnyAsync(s => s.ItemId == item.Id, cancellationToken)
            .ConfigureAwait(false);

        if (!analyzed)
        {
            stats.IncrementPushSkipped();
            return;
        }

        _logger.LogDebug("Pushing segments for \"{ItemName}\" ({Path})", item.Name, item.Path);
        try
        {
            await _mediaSegmentManager.RunSegmentPluginProviders(item, libraryOptions, forceOverwrite, cancellationToken).ConfigureAwait(false);
            stats.PushedItemIds.TryAdd(item.Id, 0);
            stats.IncrementPushed();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push segments for \"{ItemName}\" ({Path})", item.Name, item.Path);
        }
    }

    /// <summary>
    /// Checks whether a provider is disabled for the given library by matching its ID
    /// against <see cref="MediaBrowser.Model.Configuration.LibraryOptions.DisabledMediaSegmentProviders"/>.
    /// Uses the same ID derivation as Jellyfin: MD5 of the lowercased provider name.
    /// </summary>
    private static bool IsProviderDisabled(string[] disabledProviders, string providerName)
    {
        if (disabledProviders.Length == 0)
        {
            return false;
        }

        // Check both the raw provider name and its MD5 ID, since Jellyfin may store either format
        var providerId = providerName.ToLowerInvariant()
            .GetMD5()
            .ToString("N", CultureInfo.InvariantCulture);

        return Array.Exists(
            disabledProviders,
            d => string.Equals(d, providerId, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(d, providerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Force-pushes all cached segments to Jellyfin with forceOverwrite=true.
    /// Skips items that were already pushed during the normal analysis pass.
    /// </summary>
    /// <param name="alreadyPushed">Item IDs that were already pushed during this task run.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ForcePushAllSegmentsAsync(
        ConcurrentDictionary<Guid, byte> alreadyPushed,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Every analyzed item, not only those with results - same reasoning as PushSegmentsAsync.
        // Force-push exists to reconcile Jellyfin with the cache, and an item whose results are
        // gone is the one most likely to be out of sync; filtering it out made the escape hatch
        // unable to fix the very state a user would reach for it to fix.
        var itemIds = await db.AnalysisStatuses
            .Select(s => s.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Exclude items already pushed during the normal analysis pass
        var remaining = itemIds.Where(id => !alreadyPushed.ContainsKey(id)).ToList();

        _logger.LogInformation(
            "Force-pushing segments for {Count} items ({Skipped} already pushed during analysis)",
            remaining.Count,
            itemIds.Count - remaining.Count);

        var pushed = 0;
        for (int i = 0; i < remaining.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _libraryManager.GetItemById(remaining[i]);
            if (item is null)
            {
                continue;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            try
            {
                await _mediaSegmentManager.RunSegmentPluginProviders(item, libraryOptions, true, cancellationToken).ConfigureAwait(false);
                pushed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to push segments for \"{ItemName}\" ({Path})", item.Name, item.Path);
            }

            progress.Report(100.0 * (i + 1) / remaining.Count);
        }

        _logger.LogInformation("Force-push complete: {Pushed} items pushed", pushed);
    }

    private Dictionary<Guid, IReadOnlyList<BaseItem>> GetEpisodesGroupedBySeason(CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true,
            IncludeOwnedItems = true
        };

        var allEpisodes = _libraryManager.GetItemList(query);
        cancellationToken.ThrowIfCancellationRequested();

        var grouped = new Dictionary<Guid, IReadOnlyList<BaseItem>>();
        foreach (var bucket in allEpisodes
                     .OfType<Episode>()
                     .Where(e => !e.SeasonId.IsEmpty())
                     .GroupBy(e => e.SeasonId))
        {
            grouped[bucket.Key] = bucket.Cast<BaseItem>().ToList();
        }

        return grouped;
    }

    private IReadOnlyList<BaseItem> GetMovieList()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true,
            IncludeOwnedItems = true
        };

        return _libraryManager.GetItemList(query);
    }

    /// <summary>
    /// Pre-computed per-item staleness state, loaded once for the whole library so that the
    /// in-loop hot path avoids per-item EF round-trips.
    /// </summary>
    private sealed class StalenessSnapshot
    {
        public HashSet<Guid> ChapterAnalyzed { get; init; } = [];

        public HashSet<Guid> BlackFrameAnalyzed { get; init; } = [];

        public HashSet<Guid> ChromaprintAnalyzed { get; init; } = [];

        public HashSet<Guid> StaleChapterItems { get; init; } = [];

        public HashSet<Guid> StaleBlackFrameItems { get; init; } = [];

        /// <summary>
        /// Gets items whose cached black-frame samples are still valid but whose clustering /
        /// duration / refinement settings changed, so segments can be rebuilt from cache without
        /// re-running the ffmpeg scan.
        /// </summary>
        public HashSet<Guid> RebuildBlackFrameItems { get; init; } = [];

        public HashSet<Guid> ItemsWithFingerprint { get; init; } = [];

        public Dictionary<(Guid ItemId, string Region), string?> FingerprintHashes { get; init; } = [];

        /// <summary>Gets how many seconds each stored fingerprint covers.</summary>
        public Dictionary<(Guid ItemId, string Region), int> FingerprintRegions { get; init; } = [];
    }
}
