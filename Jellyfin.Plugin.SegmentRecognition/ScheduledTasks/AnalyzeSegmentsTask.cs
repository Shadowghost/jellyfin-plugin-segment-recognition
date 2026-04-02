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
    private const int PageSize = 100;

    private static readonly BaseItemKind[] _itemTypes = [BaseItemKind.Episode, BaseItemKind.Movie];

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
            _logger.LogInformation("ForceRegenerate is enabled — all segments will be re-pushed to Jellyfin after analysis");
            config.ForceRegenerate = false;
            Plugin.Instance?.SaveConfiguration();
        }

        if (config.ReanalyzeBlackFrames)
        {
            _logger.LogInformation("ReanalyzeBlackFrames is enabled — clearing all cached black frame data");
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

        var totalItems = GetTotalItemCount();
        _logger.LogInformation("Found {TotalItems} video items in library", totalItems);

        if (totalItems == 0)
        {
            _logger.LogInformation("No items to process, task complete");
            progress.Report(100);
            return;
        }

        // Single pass: analyze all items, collect groupable items (episodes by season, audio by album) for chromaprint comparison later
        // Progress: 0-70% for this pass
        _logger.LogInformation("Analyzing items and pushing non-grouped segments (parallelism={Parallelism})...", config.MaxParallelGroups);
        var itemsByGroupId = new ConcurrentDictionary<Guid, ConcurrentBag<(Guid ItemId, string Name, string? Path)>>();
        var groupsNeedingPush = new ConcurrentDictionary<Guid, byte>();
        var stats = new TaskStats();
        var processed = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(config.MaxParallelGroups, Environment.ProcessorCount),
            CancellationToken = cancellationToken
        };

        var startIndex = 0;
        while (startIndex < totalItems)
        {
            var page = GetItemPage(startIndex);
            if (page.Count == 0)
            {
                break;
            }

            await Parallel.ForEachAsync(page, parallelOptions, async (item, ct) =>
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(item);

                // Run chapter + black frame + chromaprint fingerprint analysis
                var didWork = await AnalyzeItemAsync(item, libraryOptions, config, stats, ct).ConfigureAwait(false);

                var groupId = ChromaprintProvider.GetGroupId(item);
                if (groupId != Guid.Empty)
                {
                    // Collect for group-batched chromaprint comparison later (episodes by season, audio by album)
                    var bag = itemsByGroupId.GetOrAdd(groupId, _ => []);
                    bag.Add((item.Id, item.Name, item.Path));

                    // If any item in a group had new work, the whole group needs pushing
                    // (group comparison may produce new results for other items too)
                    if (didWork)
                    {
                        groupsNeedingPush.TryAdd(groupId, 0);
                    }
                }
                else if (didWork)
                {
                    // Movies and ungrouped items: push segments immediately (only if new work was done)
                    await PushSegmentsAsync(item, libraryOptions, forceOverwrite, stats, ct).ConfigureAwait(false);
                }

                var current = Interlocked.Increment(ref processed);
                if (current % 100 == 0)
                {
                    _logger.LogDebug(
                        "Item progress: {Done}/{Total} ({ChapterNew} chapter, {BlackFrameNew} black frame, {Fingerprints} fingerprints, {MoviesPushed} movies pushed, {Skipped} skipped)",
                        current,
                        totalItems,
                        stats.ChapterAnalyzed,
                        stats.BlackFrameAnalyzed,
                        stats.FingerprintsGenerated,
                        stats.Pushed,
                        stats.AnalysisSkipped);
                }

                progress.Report(70.0 * current / totalItems);
            }).ConfigureAwait(false);

            startIndex += PageSize;
        }

        var totalGroupedItems = itemsByGroupId.Values.Sum(bag => bag.Count);
        _logger.LogInformation(
            "Item analysis complete: {ChapterAnalyzed} chapter, {BlackFrameAnalyzed} black frame, {Fingerprints} fingerprints, {Pushed} pushed, {Skipped} skipped. "
            + "{GroupedItems} items across {Groups} groups pending comparison.",
            stats.ChapterAnalyzed,
            stats.BlackFrameAnalyzed,
            stats.FingerprintsGenerated,
            stats.Pushed,
            stats.AnalysisSkipped,
            totalGroupedItems,
            itemsByGroupId.Count);

        // Group pass: chromaprint compare + push grouped items (episodes by season, audio by album)
        // Progress: 70-100%
        if (config.EnableChromaprintProvider && !itemsByGroupId.IsEmpty)
        {
            // Determine which groups need processing: new fingerprints or stale comparison results.
            var comparisonHash = ConfigHasher.ChromaprintComparison(config);
            using var stalenessDb = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var staleGroupIds = await stalenessDb.ChapterAnalysisResults
                .Where(r => (r.MatchedChapterName == SegmentSourceNames.ChromaprintIntro || r.MatchedChapterName == SegmentSourceNames.ChromaprintCredits)
                    && r.ConfigHash != comparisonHash)
                .Join(
                    stalenessDb.ChromaprintResults,
                    r => r.ItemId,
                    cr => cr.ItemId,
                    (r, cr) => cr.SeasonId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var staleGroupId in staleGroupIds)
            {
                groupsNeedingPush.TryAdd(staleGroupId, 0);
            }

            var groupsToProcess = itemsByGroupId.Keys.Where(gid => groupsNeedingPush.ContainsKey(gid)).ToList();

            _logger.LogInformation(
                "Processing {GroupCount} groups out of {TotalGroups} total (new fingerprints or stale comparison results)",
                groupsToProcess.Count,
                itemsByGroupId.Count);

            var groupsProcessed = 0;

            await Parallel.ForEachAsync(
                groupsToProcess,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, config.MaxParallelGroups),
                    CancellationToken = cancellationToken
                },
                async (gid, ct) =>
                {
                    var groupItems = itemsByGroupId[gid].ToList();
                    await ProcessGroupAsync(gid, groupItems, forceOverwrite, stats, ct).ConfigureAwait(false);

                    var done = Interlocked.Increment(ref groupsProcessed);
                    progress.Report(70.0 + (30.0 * done / Math.Max(1, groupsToProcess.Count)));
                }).ConfigureAwait(false);
        }
        else if (!config.EnableChromaprintProvider && !groupsNeedingPush.IsEmpty)
        {
            // Push grouped items that had new chapter/blackframe analysis (no chromaprint comparison needed)
            var groupIds = itemsByGroupId.Keys.Where(gid => groupsNeedingPush.ContainsKey(gid)).ToList();
            _logger.LogInformation("Chromaprint provider is disabled, pushing {Count} groups with new analysis results", groupIds.Count);

            for (int g = 0; g < groupIds.Count; g++)
            {
                var groupItems = itemsByGroupId[groupIds[g]].ToList();
                foreach (var (itemId, _, _) in groupItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var groupedItem = _libraryManager.GetItemById(itemId);
                    if (groupedItem is not null)
                    {
                        var libOpts = _libraryManager.GetLibraryOptions(groupedItem);
                        await PushSegmentsAsync(groupedItem, libOpts, forceOverwrite, stats, cancellationToken).ConfigureAwait(false);
                    }
                }

                progress.Report(70.0 + (30.0 * (g + 1) / groupIds.Count));
            }
        }

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
                            && r.ConfigHash != chapterHash,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (staleChapter)
                {
                    _logger.LogInformation("Chapter config changed for \"{ItemName}\", re-analyzing", item.Name);
                    await _chapterNameProvider.CleanupExtractedData(item.Id, cancellationToken).ConfigureAwait(false);
                    needsChapterAnalysis = true;
                }
            }

            if (needsChapterAnalysis)
            {
                _logger.LogInformation("Analyzing chapters for \"{ItemName}\" ({Path})", item.Name, item.Path);
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
                    _logger.LogInformation("BlackFrame config changed for \"{ItemName}\", re-analyzing", item.Name);
                    await _blackFrameProvider.CleanupExtractedData(item.Id, cancellationToken).ConfigureAwait(false);
                    needsBlackFrame = true;
                }
            }

            if (needsBlackFrame)
            {
                _logger.LogInformation("Analyzing black frames for \"{ItemName}\" ({Path})", item.Name, item.Path);
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

        // Chromaprint fingerprinting (generation only — comparison is done per-group later)
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

        var existing = await db.ChromaprintResults
            .FirstOrDefaultAsync(r => r.ItemId == item.Id && r.Region == region, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (string.Equals(existing.ConfigHash, expectedHash, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation(
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

    private async Task ProcessGroupAsync(
        Guid groupId,
        List<(Guid ItemId, string Name, string? Path)> groupItems,
        bool forceOverwrite,
        TaskStats stats,
        CancellationToken cancellationToken)
    {
        // Resolve a human-readable group name from the first item
        var firstItem = _libraryManager.GetItemById(groupItems[0].ItemId);
        var groupLabel = firstItem switch
        {
            Episode ep => $"\"{ep.SeriesName}\" - \"{ep.SeasonName}\"",
            _ => groupId.ToString()
        };

        // Compare all fingerprinted items in this group (fingerprints were already generated in the item pass)
        _logger.LogInformation(
            "Comparing fingerprints for {GroupLabel} ({ItemCount} items)",
            groupLabel,
            groupItems.Count);
        try
        {
            await _chromaprintProvider.AnalyzeGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
            stats.IncrementSeasonsAnalyzed();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chromaprint group analysis failed for {GroupLabel} ({GroupId})", groupLabel, groupId);
        }

        // Push segments for each item in this group
        _logger.LogInformation("Pushing segments for {GroupLabel}", groupLabel);
        foreach (var (itemId, _, _) in groupItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var groupedItem = _libraryManager.GetItemById(itemId);
            if (groupedItem is not null)
            {
                var libOpts = _libraryManager.GetLibraryOptions(groupedItem);
                await PushSegmentsAsync(groupedItem, libOpts, forceOverwrite, stats, cancellationToken).ConfigureAwait(false);
            }
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

        _logger.LogInformation("Pushing segments for \"{ItemName}\" ({Path})", item.Name, item.Path);
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

    private int GetTotalItemCount()
    {
        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            IncludeItemTypes = _itemTypes,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true
        };

        return _libraryManager.GetCount(query);
    }

    private IReadOnlyList<BaseItem> GetItemPage(int startIndex)
    {
        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            IncludeItemTypes = _itemTypes,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true,
            Limit = PageSize,
            StartIndex = startIndex
        };

        return _libraryManager.GetItemList(query);
    }
}
