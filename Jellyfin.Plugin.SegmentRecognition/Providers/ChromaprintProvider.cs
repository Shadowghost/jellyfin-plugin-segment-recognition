using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Providers;

/// <summary>
/// Detects intro/outro segments by comparing audio fingerprints across items in a group
/// (episodes in a season, tracks in an album). Fingerprints only the intro region of each item,
/// then compares pairwise using an inverted-index shift-based alignment algorithm.
/// </summary>
public class ChromaprintProvider : IMediaSegmentProvider, IHasOrder
{
    private readonly FfmpegChromaprintService _chromaprintService;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly RefinementPipeline _refinementPipeline;
    private readonly ILogger<ChromaprintProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromaprintProvider"/> class.
    /// </summary>
    /// <param name="chromaprintService">The chromaprint service.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="refinementPipeline">The refinement pipeline.</param>
    /// <param name="logger">The logger.</param>
    public ChromaprintProvider(
        FfmpegChromaprintService chromaprintService,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        RefinementPipeline refinementPipeline,
        ILogger<ChromaprintProvider> logger)
    {
        _chromaprintService = chromaprintService;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _dbContextFactory = dbContextFactory;
        _refinementPipeline = refinementPipeline;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ProviderNames.Chromaprint;

    /// <inheritdoc />
    public int Order => 2;

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return ValueTask.FromResult(config.EnableChromaprintProvider && item is Episode);
    }

    /// <summary>
    /// Removes all cached analysis data for the specified item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        db.ChromaprintResults.RemoveRange(
            db.ChromaprintResults.Where(r => r.ItemId == itemId));
        db.AnalysisStatuses.RemoveRange(
            db.AnalysisStatuses.Where(s => s.ItemId == itemId && s.ProviderName == Name));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableChromaprintProvider)
        {
            return [];
        }

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existingStatus = await db.AnalysisStatuses
            .FirstOrDefaultAsync(s => s.ItemId == request.ItemId && s.ProviderName == Name, cancellationToken)
            .ConfigureAwait(false);

        if (existingStatus is null || !existingStatus.HasResults)
        {
            return [];
        }

        var cached = await db.ChapterAnalysisResults
            .AsNoTracking()
            .Where(r => r.ItemId == request.ItemId
                && (r.MatchedChapterName == SegmentSourceNames.ChromaprintIntro
                    || r.MatchedChapterName == SegmentSourceNames.ChromaprintCredits
                    || r.MatchedChapterName == SegmentSourceNames.ChromaprintPreview))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Self-heal: if the AnalysisStatus claims HasResults=true but the cross-matched rows
        // are gone (e.g. wiped by an earlier CleanupExtractedData regression), flip HasResults
        // back to false so the next AnalyzeSegmentsTask run re-queues the group via the
        // pendingGroupIds query instead of silently serving empty segments forever.
        if (cached.Count == 0)
        {
            existingStatus.HasResults = false;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Chromaprint: self-healed stale HasResults=true on item {ItemId} (no cross-matched rows found)",
                    request.ItemId);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another writer updated the row; ignore and let them win.
            }

            return [];
        }

        return cached.Select(r => new MediaSegmentDto
        {
            ItemId = r.ItemId,
            Type = (MediaSegmentType)r.SegmentType,
            StartTicks = r.StartTicks,
            EndTicks = r.EndTicks
        }).ToList();
    }

    /// <summary>
    /// Gets the group identifier for chromaprint comparison (season for episodes).
    /// Returns <see cref="Guid.Empty"/> if the item cannot be grouped.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <returns>The group identifier, or <see cref="Guid.Empty"/>.</returns>
    public static Guid GetGroupId(BaseItem item)
    {
        if (item is Episode episode)
        {
            return episode.SeasonId;
        }

        return Guid.Empty;
    }

    /// <summary>
    /// Generates a chromaprint fingerprint for a single item and stores it in the database.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="region">The region to fingerprint ("Intro" or "Credits").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task GenerateFingerprintAsync(Guid itemId, string region, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item?.Path is null || (item.RunTimeTicks ?? 0) <= 0)
        {
            return;
        }

        var groupId = GetGroupId(item);
        if (groupId == Guid.Empty)
        {
            return;
        }

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.ChromaprintResults
            .AnyAsync(r => r.ItemId == itemId && r.Region == region, cancellationToken)
            .ConfigureAwait(false);

        if (existing)
        {
            return;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var runtimeSeconds = item.RunTimeTicks!.Value / (double)TimeSpan.TicksPerSecond;

        // For short media (≤10 minutes), fingerprint the entire file in the Intro region.
        // Skip the Credits region since it would be identical.
        var isShortMedia = runtimeSeconds <= 600;

        if (isShortMedia && string.Equals(region, SegmentSourceNames.RegionCredits, StringComparison.Ordinal))
        {
            _logger.LogDebug("Skipping credits fingerprint for short media ({Duration:F0}s) item {ItemId}", runtimeSeconds, itemId);

            // Store an empty sentinel so the task knows this was intentionally skipped
            // and doesn't retry on every run.
            var creditsHash = ConfigHasher.ChromaprintCredits(config);
            db.ChromaprintResults.Add(new ChromaprintResult
            {
                ItemId = itemId,
                Region = region,
                SeasonId = groupId,
                FingerprintData = [],
                AnalysisDurationSeconds = 0,
                ConfigHash = creditsHash,
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        double startSeconds;
        double analysisSeconds;

        if (isShortMedia)
        {
            startSeconds = 0;
            analysisSeconds = runtimeSeconds;
        }
        else if (string.Equals(region, SegmentSourceNames.RegionCredits, StringComparison.Ordinal))
        {
            // MKV containers can report a duration based on the longest stream (e.g. subtitles
            // that extend far beyond the actual audio/video). When enabled, probe the actual audio
            // stream duration to avoid seeking past the end of the audio.
            var effectiveRuntime = runtimeSeconds;
            if (config.ProbeAudioDuration)
            {
                var audioDuration = await _chromaprintService.ProbeAudioDurationAsync(item.Path, cancellationToken).ConfigureAwait(false);
                if (audioDuration.HasValue && audioDuration.Value < runtimeSeconds)
                {
                    effectiveRuntime = audioDuration.Value;
                    _logger.LogDebug(
                        "Audio duration ({AudioDuration:F1}s) differs from runtime ({Runtime:F1}s) for {ItemId}, using audio duration for credits region",
                        audioDuration.Value,
                        runtimeSeconds,
                        itemId);
                }
            }

            analysisSeconds = Math.Min(config.CreditsAnalysisDurationSeconds, effectiveRuntime);
            startSeconds = Math.Max(0, effectiveRuntime - analysisSeconds);
        }
        else
        {
            startSeconds = 0;
            analysisSeconds = Math.Min(
                runtimeSeconds * config.IntroAnalysisPercent,
                config.ChromaprintAnalysisDurationSeconds);
        }

        var fpData = await _chromaprintService.GenerateFingerprintAsync(
            item.Path,
            config.ChromaprintSampleRate,
            startSeconds,
            analysisSeconds,
            cancellationToken).ConfigureAwait(false);

        var configHash = string.Equals(region, SegmentSourceNames.RegionCredits, StringComparison.Ordinal)
            ? ConfigHasher.ChromaprintCredits(config)
            : ConfigHasher.ChromaprintIntro(config);

        if (fpData.Length == 0)
        {
            _logger.LogDebug("Chromaprint: fingerprinting produced no data for \"{ItemName}\" ({Path}) [{Region}]", item.Name, item.Path, region);

            // Store an empty sentinel so the task doesn't retry on every run.
            db.ChromaprintResults.Add(new ChromaprintResult
            {
                ItemId = itemId,
                Region = region,
                SeasonId = groupId,
                FingerprintData = [],
                AnalysisDurationSeconds = 0,
                ConfigHash = configHash,
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        db.ChromaprintResults.Add(new ChromaprintResult
        {
            ItemId = itemId,
            Region = region,
            SeasonId = groupId,
            FingerprintData = fpData,
            AnalysisDurationSeconds = (int)analysisSeconds,
            ConfigHash = configHash,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Chromaprint: generated {Region} fingerprint ({Bytes} bytes) for \"{ItemName}\" ({Path})",
            region,
            fpData.Length,
            item.Name,
            item.Path);
    }

    /// <summary>
    /// Compares all fingerprinted items in a group pairwise and writes segment results.
    /// Stores discovered segments as <see cref="ChapterAnalysisResult"/> rows so they are
    /// served by <see cref="ChapterNameProvider"/>.
    /// </summary>
    /// <param name="groupId">The group identifier (season ID or album ID).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AnalyzeGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Intro pass (exclude empty sentinels from short-media skips).
        // Use AnalysisDurationSeconds > 0 as a server-side proxy for non-empty fingerprints,
        // since SQLite can't translate byte[].Length in queries.
        var introFingerprints = await db.ChromaprintResults
            .AsNoTracking()
            .Where(r => r.SeasonId == groupId && r.Region == SegmentSourceNames.RegionIntro && r.AnalysisDurationSeconds > 0)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await AnalyzeRegionAsync(
            db,
            introFingerprints,
            MediaSegmentType.Intro,
            SegmentSourceNames.ChromaprintIntro,
            config,
            cancellationToken).ConfigureAwait(false);

        // Credits pass
        if (config.EnableCreditsFingerprinting)
        {
            var creditsFingerprints = await db.ChromaprintResults
                .AsNoTracking()
                .Where(r => r.SeasonId == groupId && r.Region == SegmentSourceNames.RegionCredits && r.AnalysisDurationSeconds > 0)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            await AnalyzeRegionAsync(
                db,
                creditsFingerprints,
                MediaSegmentType.Outro,
                SegmentSourceNames.ChromaprintCredits,
                config,
                cancellationToken).ConfigureAwait(false);
        }

        // Persist segment results before checking HasResults - the AnyAsync queries below
        // hit the database, not the change tracker, so unsaved additions would be invisible.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Set analysis status for all items that have any fingerprint in this group
        var allItemIds = await db.ChromaprintResults
            .AsNoTracking()
            .Where(r => r.SeasonId == groupId)
            .Select(r => r.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Batch-load which items have chromaprint results (2 queries instead of 2N)
        var itemsWithResults = (await db.ChapterAnalysisResults
            .Where(r => allItemIds.Contains(r.ItemId)
                && (r.MatchedChapterName == SegmentSourceNames.ChromaprintIntro || r.MatchedChapterName == SegmentSourceNames.ChromaprintCredits))
            .Select(r => r.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToHashSet();

        var existingStatuses = await db.AnalysisStatuses
            .Where(s => allItemIds.Contains(s.ItemId) && s.ProviderName == Name)
            .ToDictionaryAsync(s => s.ItemId, cancellationToken)
            .ConfigureAwait(false);

        var currentComparisonHash = ConfigHasher.ChromaprintComparison(config);

        foreach (var itemId in allItemIds)
        {
            var hasResults = itemsWithResults.Contains(itemId);

            if (existingStatuses.TryGetValue(itemId, out var existingStatus))
            {
                existingStatus.HasResults = hasResults;
                existingStatus.AnalyzedAt = DateTime.UtcNow;
                existingStatus.ConfigHash = currentComparisonHash;
            }
            else
            {
                db.AnalysisStatuses.Add(new AnalysisStatus
                {
                    ItemId = itemId,
                    ProviderName = Name,
                    AnalyzedAt = DateTime.UtcNow,
                    HasResults = hasResults,
                    ConfigHash = currentComparisonHash
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var matchCount = await db.AnalysisStatuses
            .CountAsync(s => allItemIds.Contains(s.ItemId) && s.ProviderName == Name && s.HasResults, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogDebug(
            "Chromaprint: group {GroupId} analysis complete - {Matches} items with matches out of {Total} fingerprinted",
            groupId,
            matchCount,
            allItemIds.Count);
    }

    private async Task AnalyzeRegionAsync(
        SegmentDbContext db,
        List<ChromaprintResult> fingerprints,
        MediaSegmentType segmentType,
        string matchedChapterName,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (fingerprints.Count < 2)
        {
            return;
        }

        var isCredits = string.Equals(matchedChapterName, SegmentSourceNames.ChromaprintCredits, StringComparison.Ordinal);
        var comparisonHash = ConfigHasher.ChromaprintComparison(config);

        // =================================================================================
        // Phase 1 - read existing results (no transaction, no-tracking).
        // Used to decide which fingerprints are already up-to-date and which need a rematch.
        // =================================================================================
        var fingerprintItemIds = fingerprints.Select(f => f.ItemId).Distinct().ToList();
        var existingResultsByItem = (await db.ChapterAnalysisResults
            .AsNoTracking()
            .Where(r => fingerprintItemIds.Contains(r.ItemId)
                && r.SegmentType == (int)segmentType
                && r.MatchedChapterName == matchedChapterName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(r => r.ItemId);

        // =================================================================================
        // Phase 2 - CPU + I/O (fingerprint comparison, ffmpeg silence + keyframe refinement).
        // This is the slow part (seconds to minutes for large seasons) and runs OUTSIDE any
        // transaction so it can't block concurrent group analyses on the shared SQLite file.
        // Results are accumulated into local lists and applied in Phase 3.
        // =================================================================================
        var itemsToReset = new List<Guid>();      // items whose stale rows should be deleted
        var toAdd = new List<ChapterAnalysisResult>();

        // Pass the region's own global min-duration to the comparer. No reason to have a
        // provider-specific "ChromaprintMinMatchDuration" when the intro/outro windows
        // already express exactly what a valid match length looks like - if a user widens
        // MinIntroDurationSeconds to catch short Netflix title cards the comparer should
        // surface them too, not drop them silently.
        var minMatchDurationSeconds = isCredits
            ? config.MinOutroDurationSeconds
            : config.MinIntroDurationSeconds;

        for (int i = 0; i < fingerprints.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = fingerprints[i];

            var hasExisting = existingResultsByItem.TryGetValue(current.ItemId, out var existingResult);
            if (hasExisting && string.Equals(existingResult!.ConfigHash, comparisonHash, StringComparison.Ordinal))
            {
                // Up-to-date under the current config; nothing to do for this item.
                continue;
            }

            var item = _libraryManager.GetItemById(current.ItemId);
            var runtimeTicks = item?.RunTimeTicks ?? 0;
            if (runtimeTicks <= 0)
            {
                continue;
            }

            MediaSegmentDto? bestMatch = null;

            for (int j = 0; j < fingerprints.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var other = fingerprints[j];
                var matchedRegions = FingerprintComparer.FindMatchedRegions(
                    current.FingerprintData,
                    other.FingerprintData,
                    config.ChromaprintMaxBitErrors,
                    config.ChromaprintMaxTimeSkipSeconds,
                    config.ChromaprintInvertedIndexShift,
                    minMatchDurationSeconds,
                    cancellationToken);

                foreach (var (startTicks, endTicks) in matchedRegions)
                {
                    var durationSeconds = (endTicks - startTicks) / (double)TimeSpan.TicksPerSecond;

                    if (isCredits)
                    {
                        if (durationSeconds < config.MinOutroDurationSeconds || durationSeconds > config.MaxOutroDurationSeconds)
                        {
                            continue;
                        }

                        // For credits, compute absolute position: the fingerprint starts at (runtime - analysisDuration)
                        var offsetTicks = Math.Max(0, runtimeTicks - (current.AnalysisDurationSeconds * TimeSpan.TicksPerSecond));
                        var absStart = offsetTicks + startTicks;
                        var absEnd = offsetTicks + endTicks;

                        // Credits should be in the second half of the file
                        if (absStart > runtimeTicks / 2)
                        {
                            bestMatch = new MediaSegmentDto
                            {
                                ItemId = current.ItemId,
                                Type = MediaSegmentType.Outro,
                                StartTicks = absStart,
                                EndTicks = absEnd
                            };

                            break;
                        }
                    }
                    else
                    {
                        if (durationSeconds < config.MinIntroDurationSeconds || durationSeconds > config.MaxIntroDurationSeconds)
                        {
                            continue;
                        }

                        if (startTicks < runtimeTicks / 2)
                        {
                            bestMatch = new MediaSegmentDto
                            {
                                ItemId = current.ItemId,
                                Type = MediaSegmentType.Intro,
                                StartTicks = startTicks,
                                EndTicks = endTicks
                            };

                            break;
                        }
                    }
                }

                if (bestMatch is not null)
                {
                    break;
                }
            }

            if (hasExisting)
            {
                // Flag stale rows for deletion in the apply phase regardless of whether a
                // new match was found: a rematch that fails must clear the old segment too.
                itemsToReset.Add(current.ItemId);
            }

            if (bestMatch is not null)
            {
                var videoCodec = _mediaSourceManager.GetMediaStreams(current.ItemId)
                    .FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Codec;

                var (refinedStart, refinedEnd) = await _refinementPipeline.RefineAsync(
                    current.ItemId,
                    bestMatch.StartTicks,
                    bestMatch.EndTicks,
                    item!.Path!,
                    videoCodec,
                    cancellationToken).ConfigureAwait(false);

                // If this is an outro/credits that ends before the episode's runtime, the
                // trailing portion is either a real next-episode teaser or just a couple of
                // seconds of black/silence before EOF. A "Preview" segment shorter than
                // MinPreviewDurationSeconds is almost certainly the latter - surface it as
                // part of the outro instead of a misleading 1-second preview entry. The
                // upper cap guards against treating long post-credits scenes as previews.
                if (config.EnablePreviewInference && isCredits && refinedEnd < runtimeTicks)
                {
                    var trailingSeconds = (runtimeTicks - refinedEnd) / (double)TimeSpan.TicksPerSecond;
                    if (trailingSeconds < config.MinPreviewDurationSeconds)
                    {
                        _logger.LogDebug(
                            "Absorbing {Duration:F1}s post-credits tail into outro for item {ItemId} (below {Threshold}s preview threshold)",
                            trailingSeconds,
                            current.ItemId,
                            config.MinPreviewDurationSeconds);
                        refinedEnd = runtimeTicks;
                    }
                }

                toAdd.Add(new ChapterAnalysisResult
                {
                    ItemId = current.ItemId,
                    SegmentType = (int)segmentType,
                    StartTicks = refinedStart,
                    EndTicks = refinedEnd,
                    MatchedChapterName = matchedChapterName,
                    ConfigHash = comparisonHash,
                    CreatedAt = DateTime.UtcNow
                });

                if (config.EnablePreviewInference && isCredits && refinedEnd < runtimeTicks)
                {
                    var previewDurationSeconds = (runtimeTicks - refinedEnd) / (double)TimeSpan.TicksPerSecond;
                    if (previewDurationSeconds <= config.MaxPreviewDurationSeconds)
                    {
                        _logger.LogDebug(
                            "Detected {Duration:F1}s preview after credits for item {ItemId}",
                            previewDurationSeconds,
                            current.ItemId);

                        toAdd.Add(new ChapterAnalysisResult
                        {
                            ItemId = current.ItemId,
                            SegmentType = (int)MediaSegmentType.Preview,
                            StartTicks = refinedEnd,
                            EndTicks = runtimeTicks,
                            MatchedChapterName = SegmentSourceNames.ChromaprintPreview,
                            ConfigHash = comparisonHash,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        if (itemsToReset.Count == 0 && toAdd.Count == 0)
        {
            return;
        }

        // =================================================================================
        // Phase 3 - apply the planned writes in a short transaction.
        // The stale-delete + insert pair must be atomic so a rematch never leaves the DB
        // with both the old row and the new one (or neither). Deletes use ExecuteDeleteAsync
        // so no entities have to be re-fetched/tracked.
        // =================================================================================
        using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (itemsToReset.Count > 0)
        {
            await db.ChapterAnalysisResults
                .Where(r => itemsToReset.Contains(r.ItemId)
                    && r.SegmentType == (int)segmentType
                    && r.MatchedChapterName == matchedChapterName)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (isCredits)
            {
                // Preview rows are derived from credits; purge them in lock-step.
                await db.ChapterAnalysisResults
                    .Where(r => itemsToReset.Contains(r.ItemId)
                        && r.SegmentType == (int)MediaSegmentType.Preview
                        && r.MatchedChapterName == SegmentSourceNames.ChromaprintPreview)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (toAdd.Count > 0)
        {
            db.ChapterAnalysisResults.AddRange(toAdd);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
