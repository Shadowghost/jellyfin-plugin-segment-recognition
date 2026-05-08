using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
            config.ForceRegenerate = false;
            Plugin.Instance?.SaveConfiguration();
        }

        if (config.ReanalyzeBlackFrames)
        {
            _logger.LogInformation("ReanalyzeBlackFrames is enabled - clearing all cached black frame data");
            config.ReanalyzeBlackFrames = false;
            Plugin.Instance?.SaveConfiguration();

            using var cleanupDb = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await cleanupDb.BlackFrameResults.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await cleanupDb.CropDetectResults.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await cleanupDb.AnalysisStatuses
                .Where(s => s.ProviderName == ProviderNames.BlackFrame)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        progress.Report(0);

        // Iterate Series → (all episodes of series, grouped by SeasonId in memory).
        // One episode query per series instead of one per season; total query count is bounded
        // by the number of series rather than the number of seasons.
        var seriesList = GetSeriesList();
        var movies = GetMovieList();
        var totalItems = GetEpisodeCount() + movies.Count;

        _logger.LogInformation(
            "Found {Series} series (~{Episodes} episodes) and {Movies} movies in library",
            seriesList.Count,
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

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(config.MaxParallelGroups, Environment.ProcessorCount)),
            CancellationToken = cancellationToken
        };

        // Process each series in parallel; within a series, fetch all episodes once, group by
        // SeasonId, and run each season end-to-end (analyze → chromaprint compare → push).
        await Parallel.ForEachAsync(seriesList, parallelOptions, async (series, ct) =>
        {
            var seasonEpisodes = GroupEpisodesBySeason(GetEpisodesInSeries(series.Id));
            if (seasonEpisodes.Count == 0)
            {
                return;
            }

            foreach (var (seasonId, episodes) in seasonEpisodes)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessSeasonAsync(seasonId, episodes, config, forceOverwrite, stats, ct).ConfigureAwait(false);

                var current = Interlocked.Add(ref processed, episodes.Count);
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
            }
        }).ConfigureAwait(false);

        // Movies: no grouping, no comparison - analyze and push each independently.
        await Parallel.ForEachAsync(movies, parallelOptions, async (movie, ct) =>
        {
            await ProcessMovieAsync(movie, config, forceOverwrite, stats, ct).ConfigureAwait(false);

            var current = Interlocked.Increment(ref processed);
            progress.Report(Math.Min(99.0, 100.0 * current / totalItems));
        }).ConfigureAwait(false);

        // If ForceRegenerate was set, re-push ALL items with cached results (not just newly analyzed ones)
        if (forceOverwrite)
        {
            await ForcePushAllSegmentsAsync(stats.PushedItemIds, progress, cancellationToken).ConfigureAwait(false);
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
    /// Runs all analysis pipelines for one season's episodes, then (if needed) the chromaprint
    /// group comparison, then pushes segments for every episode in the season.
    /// </summary>
    private async Task ProcessSeasonAsync(
        Guid seasonId,
        IReadOnlyList<BaseItem> episodes,
        PluginConfiguration config,
        bool forceOverwrite,
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

        // 1) Analyze each episode (chapters + blackframe + chromaprint fingerprint generation).
        var anyNewWork = false;
        foreach (var ep in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraryOptions = _libraryManager.GetLibraryOptions(ep);
            if (await AnalyzeItemAsync(ep, libraryOptions, config, stats, cancellationToken).ConfigureAwait(false))
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
        }

        // 3) Push segments. Comparison may produce results for episodes that had no new work
        //    themselves, so push the whole season whenever we did any new work or ran comparison.
        if (anyNewWork || ranComparison)
        {
            _logger.LogDebug("Pushing segments for {Label}", seasonLabel);
            foreach (var ep in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var libraryOptions = _libraryManager.GetLibraryOptions(ep);
                await PushSegmentsAsync(ep, libraryOptions, forceOverwrite, stats, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessMovieAsync(
        BaseItem movie,
        PluginConfiguration config,
        bool forceOverwrite,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        var libraryOptions = _libraryManager.GetLibraryOptions(movie);
        if (await AnalyzeItemAsync(movie, libraryOptions, config, stats, cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// Runs all applicable analysis pipelines for a single item.
    /// Returns <c>true</c> if any new analysis work was performed (meaning segments should be pushed).
    /// </summary>
    private async Task<bool> AnalyzeItemAsync(
        BaseItem item,
        MediaBrowser.Model.Configuration.LibraryOptions libraryOptions,
        PluginConfiguration config,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var snapshotBefore = stats.TotalWork;
        var disabledProviders = libraryOptions.DisabledMediaSegmentProviders;

        _logger.LogDebug(
            "Item \"{ItemName}\": DisabledMediaSegmentProviders=[{Disabled}]",
            item.Name,
            string.Join(", ", disabledProviders));

        // Chapter name analysis
        if (config.EnableChapterNameProvider && !IsProviderDisabled(disabledProviders, ProviderNames.ChapterName))
        {
            var needsChapterAnalysis = !await db.AnalysisStatuses
                .AnyAsync(s => s.ItemId == item.Id && s.ProviderName == ProviderNames.ChapterName, cancellationToken)
                .ConfigureAwait(false);

            if (!needsChapterAnalysis)
            {
                // Check for stale results from a different config
                var chapterHash = ConfigHasher.ChapterName(config);
                var staleChapter = await db.ChapterAnalysisResults
                    .AnyAsync(
                        r => r.ItemId == item.Id
                            && r.MatchedChapterName != SegmentSourceNames.ChromaprintIntro
                            && r.MatchedChapterName != SegmentSourceNames.ChromaprintCredits
                            && r.MatchedChapterName != SegmentSourceNames.ChromaprintPreview
                            && r.MatchedChapterName != EdlImportProvider.MatchedName
                            && r.MatchedChapterName != ImportIntroSkipperDataTask.MatchedName
                            && r.ConfigHash != chapterHash,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (staleChapter)
                {
                    _logger.LogDebug("Chapter config changed for \"{ItemName}\", re-analyzing", item.Name);
                    await _chapterNameProvider.CleanupExtractedData(item.Id, cancellationToken).ConfigureAwait(false);
                    needsChapterAnalysis = true;
                }
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
            var needsBlackFrame = !await db.AnalysisStatuses
                .AnyAsync(s => s.ItemId == item.Id && s.ProviderName == ProviderNames.BlackFrame, cancellationToken)
                .ConfigureAwait(false);

            if (!needsBlackFrame)
            {
                // Check for stale results from a different config
                var bfHash = ConfigHasher.BlackFrame(config);
                var staleBf = await db.BlackFrameResults
                    .AnyAsync(r => r.ItemId == item.Id && r.ConfigHash != bfHash, cancellationToken)
                    .ConfigureAwait(false);

                if (staleBf)
                {
                    _logger.LogDebug("BlackFrame config changed for \"{ItemName}\", re-analyzing", item.Name);
                    await _blackFrameProvider.CleanupExtractedData(item.Id, cancellationToken).ConfigureAwait(false);
                    needsBlackFrame = true;
                }
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
        }

        // Chromaprint fingerprinting (generation only - comparison is done per-group later)
        if (config.EnableChromaprintProvider && !IsProviderDisabled(disabledProviders, ProviderNames.Chromaprint)
            && ChromaprintProvider.GetGroupId(item) != Guid.Empty)
        {
            await EnsureChromaprintFingerprintAsync(db, item, SegmentSourceNames.RegionIntro, config, stats, cancellationToken).ConfigureAwait(false);

            if (config.EnableCreditsFingerprinting)
            {
                await EnsureChromaprintFingerprintAsync(db, item, SegmentSourceNames.RegionCredits, config, stats, cancellationToken).ConfigureAwait(false);
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
        SegmentDbContext db,
        BaseItem item,
        string region,
        PluginConfiguration config,
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
        var hasAnyFingerprint = await db.ChromaprintResults
            .AnyAsync(r => r.ItemId == item.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!hasAnyFingerprint)
        {
            var hasStatus = await db.AnalysisStatuses
                .AnyAsync(s => s.ItemId == item.Id && s.ProviderName == ProviderNames.Chromaprint, cancellationToken)
                .ConfigureAwait(false);
            if (hasStatus)
            {
                return;
            }
        }

        var existing = await db.ChromaprintResults
            .FirstOrDefaultAsync(r => r.ItemId == item.Id && r.Region == region, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (string.Equals(existing.ConfigHash, expectedHash, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogDebug(
                "Chromaprint {Region} config changed for \"{ItemName}\", regenerating fingerprint",
                region,
                item.Name);
            db.ChromaprintResults.Remove(existing);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task PushSegmentsAsync(
        BaseItem item,
        MediaBrowser.Model.Configuration.LibraryOptions libraryOptions,
        bool forceOverwrite,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var hasResults = await db.AnalysisStatuses
            .AnyAsync(s => s.ItemId == item.Id && s.HasResults, cancellationToken)
            .ConfigureAwait(false);

        if (!hasResults)
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

        var itemIds = await db.AnalysisStatuses
            .Where(s => s.HasResults)
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

    private IReadOnlyList<BaseItem> GetSeriesList()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true
        };

        return _libraryManager.GetItemList(query);
    }

    private IReadOnlyList<BaseItem> GetEpisodesInSeries(Guid seriesId)
    {
        var query = new InternalItemsQuery
        {
            ParentId = seriesId,
            IncludeItemTypes = [BaseItemKind.Episode],
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true
        };

        return _libraryManager.GetItemList(query);
    }

    private int GetEpisodeCount()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true
        };

        return _libraryManager.GetCount(query);
    }

    private static Dictionary<Guid, IReadOnlyList<BaseItem>> GroupEpisodesBySeason(IReadOnlyList<BaseItem> episodes)
    {
        var grouped = new Dictionary<Guid, List<BaseItem>>();
        foreach (var ep in episodes)
        {
            var groupId = ChromaprintProvider.GetGroupId(ep);
            if (groupId == Guid.Empty)
            {
                continue;
            }

            if (!grouped.TryGetValue(groupId, out var list))
            {
                list = [];
                grouped[groupId] = list;
            }

            list.Add(ep);
        }

        var result = new Dictionary<Guid, IReadOnlyList<BaseItem>>(grouped.Count);
        foreach (var (sid, eps) in grouped)
        {
            result[sid] = eps;
        }

        return result;
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
            Recursive = true
        };

        return _libraryManager.GetItemList(query);
    }
}
