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
    /// <summary>
    /// Maximum number of counterparts a single item is compared against.
    /// <para>
    /// Consensus needs several opinions but not every one: an exhaustive pairwise pass is
    /// O(N²) per season and each comparison is a full Hamming scan, which on a large season with
    /// no real match degenerates badly. A handful of counterparts is ample for a majority vote
    /// and bounds the work at O(N).
    /// </para>
    /// <para>
    /// Which counterparts those are matters as much as how many - see
    /// <see cref="NeighboursByDistance"/>.
    /// </para>
    /// </summary>
    private const int MaxCounterpartsPerItem = 8;

    /// <summary>
    /// How far apart two candidate regions may start and still be treated as the same region.
    /// Fingerprint points are ~0.124 s apart and encodes differ slightly between episodes, so
    /// agreeing matches rarely land on exactly the same tick.
    /// </summary>
    private const long ConsensusToleranceTicks = 2 * TimeSpan.TicksPerSecond;

    /// <summary>
    /// How far apart two episodes' intros may start and still count as the same season position.
    /// Generous: what it separates is "after the same cold open" from "somewhere else entirely",
    /// and cold-open lengths drift by tens of seconds within a season.
    /// </summary>
    private const long IntroPositionToleranceTicks = 120 * TimeSpan.TicksPerSecond;

    /// <summary>
    /// The same tolerance for outros, which are measured back from the end of the file.
    /// <para>
    /// Much tighter than the intro's, because the two positions are set by different things: an
    /// intro's offset depends on the cold open, which varies episode to episode, while an outro's
    /// distance from the end is the length of the credits, which a season keeps fixed. The whole
    /// outro population also lives inside the last <c>CreditsAnalysisDurationSeconds</c> of the
    /// file, so the intro's 120 s would span half the searchable window and cluster everything
    /// together.
    /// </para>
    /// </summary>
    private const long OutroPositionToleranceTicks = 30 * TimeSpan.TicksPerSecond;

    /// <summary>
    /// How many episodes must share a position before it counts as a format variant rather than
    /// noise. Two episodes agreeing is exactly what a spurious shared music cue produces - it
    /// takes two counterparts to pass consensus in the first place - so the bar sits above it.
    /// </summary>
    private const int MinSeasonClusterSize = 3;

    /// <summary>
    /// Fraction of a season's intros that must share one position before the remainder can be
    /// treated as stragglers. See <see cref="SelectSeasonOutliers"/>.
    /// </summary>
    private const double SeasonDominanceFraction = 0.6;

    /// <summary>
    /// Below this many segments a season carries too little evidence to judge any of them: a
    /// cluster of three out of five is a "majority" that means nothing.
    /// </summary>
    private const int MinSegmentsForSeasonPruning = 6;

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

        try
        {
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

            // Self-heal if the AnalysisStatus claims HasResults=true but the cross-matched rows are gone
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
        catch (ObjectDisposedException)
        {
            // Host is shutting down - the DbContextFactory's underlying service provider has been
            // disposed. Return empty rather than letting MediaSegmentManager log this as a failure.
            return [];
        }
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
                RegionStartTicks = 0,
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
                RegionStartTicks = 0,
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

            // Record where the fingerprint actually starts. Re-deriving this later from
            // (runtime - AnalysisDurationSeconds) was wrong twice over: the credits region is
            // anchored to the audio duration when ProbeAudioDuration is on, and the stored
            // duration is truncated to whole seconds.
            RegionStartTicks = (long)(startSeconds * TimeSpan.TicksPerSecond),
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

        introFingerprints = FilterOutOrphans(introFingerprints, groupId, SegmentSourceNames.RegionIntro);

        var introOutcomes = await AnalyzeRegionAsync(
            db,
            introFingerprints,
            MediaSegmentType.Intro,
            SegmentSourceNames.ChromaprintIntro,
            config,
            cancellationToken).ConfigureAwait(false);

        var outroOutcomes = new Dictionary<Guid, SegmentMatchOutcome>();

        // Credits pass
        if (config.EnableCreditsFingerprinting)
        {
            var creditsFingerprints = await db.ChromaprintResults
                .AsNoTracking()
                .Where(r => r.SeasonId == groupId && r.Region == SegmentSourceNames.RegionCredits && r.AnalysisDurationSeconds > 0)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            creditsFingerprints = FilterOutOrphans(creditsFingerprints, groupId, SegmentSourceNames.RegionCredits);

            outroOutcomes = await AnalyzeRegionAsync(
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

        // Batch-load which items have chromaprint results. Chunked by SQLite's parameter cap,
        // which EF expands Contains() into one bind per id.
        var itemsWithResults = new HashSet<Guid>();
        var existingStatuses = new Dictionary<Guid, AnalysisStatus>();
        foreach (var chunk in allItemIds.Chunk(500))
        {
            itemsWithResults.UnionWith(await db.ChapterAnalysisResults
                .Where(r => chunk.Contains(r.ItemId)
                    && (r.MatchedChapterName == SegmentSourceNames.ChromaprintIntro || r.MatchedChapterName == SegmentSourceNames.ChromaprintCredits))
                .Select(r => r.ItemId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));

            var statuses = await db.AnalysisStatuses
                .Where(s => chunk.Contains(s.ItemId) && s.ProviderName == Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var status in statuses)
            {
                existingStatuses[status.ItemId] = status;
            }
        }

        var currentComparisonHash = ConfigHasher.ChromaprintComparison(config);
        var lostResults = new List<Guid>();

        foreach (var itemId in allItemIds)
        {
            var hasResults = itemsWithResults.Contains(itemId);

            // Only items this run actually evaluated appear in the outcome maps; an item skipped
            // because its stored result is still current keeps whatever outcome it already had.
            var introOutcome = introOutcomes.TryGetValue(itemId, out var io) ? io : (SegmentMatchOutcome?)null;
            var outroOutcome = outroOutcomes.TryGetValue(itemId, out var oo) ? oo : (SegmentMatchOutcome?)null;

            if (existingStatuses.TryGetValue(itemId, out var existingStatus))
            {
                if (existingStatus.HasResults && !hasResults)
                {
                    lostResults.Add(itemId);
                }

                existingStatus.HasResults = hasResults;
                existingStatus.AnalyzedAt = DateTime.UtcNow;
                existingStatus.ConfigHash = currentComparisonHash;

                if (introOutcome is not null)
                {
                    existingStatus.IntroOutcome = introOutcome;
                }

                if (outroOutcome is not null)
                {
                    existingStatus.OutroOutcome = outroOutcome;
                }
            }
            else
            {
                db.AnalysisStatuses.Add(new AnalysisStatus
                {
                    ItemId = itemId,
                    ProviderName = Name,
                    AnalyzedAt = DateTime.UtcNow,
                    HasResults = hasResults,
                    ConfigHash = currentComparisonHash,
                    IntroOutcome = introOutcome,
                    OutroOutcome = outroOutcome
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var matchCount = 0;
        foreach (var chunk in allItemIds.Chunk(500))
        {
            matchCount += await db.AnalysisStatuses
                .CountAsync(s => chunk.Contains(s.ItemId) && s.ProviderName == Name && s.HasResults, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Chromaprint: group {GroupId} analysis complete - {Matches} items with matches out of {Total} fingerprinted",
            groupId,
            matchCount,
            allItemIds.Count);

        if (lostResults.Count > 0)
        {
            // Worth surfacing on its own: these items keep whatever Jellyfin is already serving
            // until the task pushes them again, which is why the push gate keys on "analyzed"
            // rather than "has results".
            _logger.LogDebug(
                "Chromaprint: {Count} item(s) in group {GroupId} lost their segments",
                lostResults.Count,
                groupId);
        }
    }

    /// <summary>
    /// Compares one region across a group and writes the agreed segments.
    /// </summary>
    /// <returns>
    /// Why each item this run evaluated did or did not get a segment. Items skipped because
    /// their stored result is still current are absent, so the caller leaves their recorded
    /// outcome alone.
    /// </returns>
    private async Task<Dictionary<Guid, SegmentMatchOutcome>> AnalyzeRegionAsync(
        SegmentDbContext db,
        List<ChromaprintResult> fingerprints,
        MediaSegmentType segmentType,
        string matchedChapterName,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var outcomes = new Dictionary<Guid, SegmentMatchOutcome>();

        if (fingerprints.Count < 2)
        {
            // A lone fingerprint has nothing to be compared against; say so rather than leaving
            // the item indistinguishable from one that was never analyzed.
            foreach (var only in fingerprints)
            {
                outcomes[only.ItemId] = SegmentMatchOutcome.NoComparableCounterparts;
            }

            return outcomes;
        }

        var isCredits = string.Equals(matchedChapterName, SegmentSourceNames.ChromaprintCredits, StringComparison.Ordinal);
        var comparisonHash = ConfigHasher.ChromaprintComparison(config);

        // Put the group in episode order so "counterpart" can mean "a nearby episode" below.
        // The query that produced this list has no ORDER BY, so its order was whatever SQLite
        // happened to return - which made every item's result depend on row order.
        fingerprints = OrderByEpisode(fingerprints);

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

        // Alternate versions of the same title share (near-)identical audio, so matching a
        // version against its own sibling yields a degenerate whole-window match instead of
        // the common intro/credits. Map each fingerprint to its logical title (the primary
        // version) and only compare fingerprints of *different* titles.
        var titleIdByItem = new Dictionary<Guid, Guid>(fingerprintItemIds.Count);
        foreach (var id in fingerprintItemIds)
        {
            titleIdByItem[id] = _libraryManager.GetItemById(id) is Video { PrimaryVersionId: { } primaryId }
                && primaryId != Guid.Empty
                ? primaryId
                : id;
        }

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

            // The offset of the fingerprint within the file. Stored explicitly since the credits
            // region is anchored to the audio duration, which can be shorter than the container
            // runtime. Rows written before that column existed carry 0; for credits that is not a
            // possible real value (a credits fingerprint never starts at 0 - short media is
            // fingerprinted whole under the Intro region), so fall back to the old derivation.
            var regionOffsetTicks = current.RegionStartTicks;
            if (isCredits && regionOffsetTicks == 0)
            {
                regionOffsetTicks = Math.Max(0, runtimeTicks - (current.AnalysisDurationSeconds * TimeSpan.TicksPerSecond));
            }

            // Collect one candidate region per counterpart, then take the position that the most
            // counterparts agree on. Accepting the first counterpart that happened to produce an
            // in-window region made the result depend on row order and let a single spurious
            // pairing define the segment for the whole item.
            var candidates = new List<(long StartTicks, long EndTicks)>();
            var counterpartsCompared = 0;

            // Tracked so a failure can say whether nothing was shared at all or whether shared
            // audio was found and then rejected by the duration/position windows.
            var anySharedRegion = false;

            foreach (var j in NeighboursByDistance(i, fingerprints.Count))
            {
                if (counterpartsCompared >= MaxCounterpartsPerItem)
                {
                    break;
                }

                var other = fingerprints[j];

                // Never match a title against another version of itself (see titleIdByItem).
                if (titleIdByItem[current.ItemId] == titleIdByItem[other.ItemId])
                {
                    continue;
                }

                counterpartsCompared++;

                var matchedRegions = FingerprintComparer.FindMatchedRegions(
                    current.FingerprintData,
                    other.FingerprintData,
                    config.ChromaprintMaxBitErrors,
                    config.ChromaprintMaxTimeSkipSeconds,
                    config.ChromaprintInvertedIndexShift,
                    minMatchDurationSeconds,
                    cancellationToken);

                anySharedRegion |= matchedRegions.Count > 0;

                foreach (var (startTicks, endTicks) in matchedRegions)
                {
                    var durationSeconds = (endTicks - startTicks) / (double)TimeSpan.TicksPerSecond;
                    var absStart = regionOffsetTicks + startTicks;
                    var absEnd = regionOffsetTicks + endTicks;

                    if (isCredits)
                    {
                        if (durationSeconds < config.MinOutroDurationSeconds
                            || durationSeconds > config.MaxOutroDurationSeconds)
                        {
                            continue;
                        }

                        // Credits should be in the second half of the file.
                        if (absStart <= runtimeTicks / 2)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (durationSeconds < config.MinIntroDurationSeconds
                            || durationSeconds > config.MaxIntroDurationSeconds)
                        {
                            continue;
                        }

                        if (absStart >= runtimeTicks / 2)
                        {
                            continue;
                        }
                    }

                    // One vote per counterpart: the best (longest) in-window region it produced.
                    candidates.Add((absStart, absEnd));
                    break;
                }
            }

            var consensus = SelectConsensusRegion(candidates, counterpartsCompared);

            outcomes[current.ItemId] = ClassifyOutcome(
                consensus is not null,
                counterpartsCompared,
                anySharedRegion,
                candidates.Count);

            var bestMatch = consensus is null
                ? null
                : new MediaSegmentDto
                {
                    ItemId = current.ItemId,
                    Type = segmentType,
                    StartTicks = consensus.Value.StartTicks,
                    EndTicks = consensus.Value.EndTicks
                };

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
                    cancellationToken,
                    minMatchDurationSeconds).ConfigureAwait(false);

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

        if (config.EnableSeasonOutlierPruning)
        {
            PruneSeasonOutliers(
                matchedChapterName,
                segmentType,
                isCredits,
                existingResultsByItem,
                itemsToReset,
                toAdd,
                outcomes);
        }

        if (itemsToReset.Count == 0 && toAdd.Count == 0)
        {
            return outcomes;
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

        return outcomes;
    }

    /// <summary>
    /// Applies <see cref="SelectSeasonOutliers"/> to the whole season and removes what it flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The population has to span the season, not just the items re-evaluated on this run:
    /// judging a straggler needs the positions its siblings agreed on, and on an incremental run
    /// most of those come from rows written earlier. Rows belonging to items already queued for
    /// deletion are excluded - they are about to be replaced by the entries in
    /// <paramref name="toAdd"/>, which are counted instead.
    /// </para>
    /// <para>
    /// Intros are positioned from the start of the file and outros from the end, because that is
    /// what each one is actually anchored to. Runtimes vary widely inside a season - by more than
    /// 15 minutes in the top decile of the sample library - so measuring a credits sequence from
    /// the start of the file scatters a season that is in fact perfectly consistent: on that
    /// library only 57% of seasons showed a dominant outro position start-relative, against 96%
    /// end-relative.
    /// </para>
    /// </remarks>
    private void PruneSeasonOutliers(
        string matchedChapterName,
        MediaSegmentType segmentType,
        bool isCredits,
        Dictionary<Guid, ChapterAnalysisResult> existingResultsByItem,
        List<Guid> itemsToReset,
        List<ChapterAnalysisResult> toAdd,
        Dictionary<Guid, SegmentMatchOutcome> outcomes)
    {
        var replaced = itemsToReset.ToHashSet();
        var freshByItem = toAdd
            .Where(r => string.Equals(r.MatchedChapterName, matchedChapterName, StringComparison.Ordinal))
            .ToDictionary(r => r.ItemId);

        var candidates = freshByItem
            .Select(kvp => (ItemId: kvp.Key, kvp.Value.StartTicks))
            .Concat(existingResultsByItem
                .Where(kvp => !replaced.Contains(kvp.Key) && !freshByItem.ContainsKey(kvp.Key))
                .Select(kvp => (ItemId: kvp.Key, kvp.Value.StartTicks)));

        var population = new List<(Guid ItemId, long PositionTicks)>();
        foreach (var (itemId, startTicks) in candidates)
        {
            if (!isCredits)
            {
                population.Add((itemId, startTicks));
                continue;
            }

            // An outro's position is its distance back from the end of the file. An item whose
            // runtime is unknown has no such coordinate, so it cannot be judged either way.
            var runtimeTicks = _libraryManager.GetItemById(itemId)?.RunTimeTicks ?? 0;
            if (runtimeTicks > 0)
            {
                population.Add((itemId, runtimeTicks - startTicks));
            }
        }

        var outliers = SelectSeasonOutliers(
            population,
            isCredits ? OutroPositionToleranceTicks : IntroPositionToleranceTicks);

        if (outliers.Count == 0)
        {
            return;
        }

        foreach (var itemId in outliers)
        {
            if (freshByItem.TryGetValue(itemId, out var fresh))
            {
                toAdd.Remove(fresh);

                // Preview rows are inferred from the credits boundary, so a discarded outro must
                // take its preview with it. Rows written by an earlier run are already handled:
                // the credits branch of the stale-delete purges previews in lock-step.
                if (isCredits)
                {
                    toAdd.RemoveAll(r => r.ItemId == itemId
                        && string.Equals(r.MatchedChapterName, SegmentSourceNames.ChromaprintPreview, StringComparison.Ordinal));
                }
            }
            else
            {
                // Written by an earlier run and untouched by this one, so it is only removed by
                // being queued for deletion here.
                itemsToReset.Add(itemId);
            }

            outcomes[itemId] = SegmentMatchOutcome.SeasonOutlier;
        }

        _logger.LogDebug(
            "Chromaprint: dropped {Count} {Segment} segment(s) sitting at a position the rest of the season does not share",
            outliers.Count,
            segmentType);
    }

    /// <summary>
    /// Picks the segments in a season that sit at a position essentially no other episode shares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An episode that has no intro of its own can still produce one: with nothing at the front to
    /// lock onto, the matcher falls through to whatever incidental music cue it happens to share
    /// with a sibling, and a cue corroborated by two counterparts passes consensus. Those land at
    /// arbitrary positions - 217 s, 490 s, 503 s in a season where seventeen episodes agree on 0 s.
    /// </para>
    /// <para>
    /// The test is deliberately <em>not</em> distance from the season's average position. Seasons
    /// legitimately carry several intro positions, because the cold open before the titles varies:
    /// one 197-episode arc in the sample library splits 141 episodes at 0 s, 41 at 196 s and 15 at
    /// 271 s, all correct. Rejecting on distance would have discarded 56 good intros there. What
    /// separates a format variant from noise is not where it sits but how many episodes
    /// independently landed on it, so this clusters the season's positions and drops only the
    /// clusters too small to be a variant.
    /// </para>
    /// <para>
    /// Pruning is skipped entirely unless one cluster holds a clear majority of the season. Without
    /// a dominant position there is no established format for a straggler to be a straggler
    /// <em>from</em> - the season's matching is simply unreliable, and guessing which scattered
    /// results are wrong would remove as many good ones as bad.
    /// </para>
    /// </remarks>
    /// <param name="population">
    /// Every segment of this kind in the season, as (item, position). The position is measured in
    /// whichever direction the segment is anchored - see <see cref="PruneSeasonOutliers"/>.
    /// </param>
    /// <param name="toleranceTicks">How far apart two positions may be and still count as one.</param>
    /// <returns>The items whose segment should be discarded.</returns>
    internal static IReadOnlyCollection<Guid> SelectSeasonOutliers(
        IReadOnlyList<(Guid ItemId, long PositionTicks)> population,
        long toleranceTicks)
    {
        ArgumentNullException.ThrowIfNull(population);

        if (population.Count < MinSegmentsForSeasonPruning)
        {
            return [];
        }

        var ordered = population.OrderBy(p => p.PositionTicks).ToList();

        // Greedy chaining against each cluster's first member, matching SelectConsensusRegion.
        var clusters = new List<List<(Guid ItemId, long PositionTicks)>>();
        var current = new List<(Guid ItemId, long PositionTicks)> { ordered[0] };
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].PositionTicks - current[0].PositionTicks <= toleranceTicks)
            {
                current.Add(ordered[i]);
                continue;
            }

            clusters.Add(current);
            current = [ordered[i]];
        }

        clusters.Add(current);

        var largest = clusters.Max(c => c.Count);
        if (largest < population.Count * SeasonDominanceFraction)
        {
            return [];
        }

        return clusters
            .Where(c => c.Count < MinSeasonClusterSize)
            .SelectMany(c => c)
            .Select(c => c.ItemId)
            .ToList();
    }

    /// <summary>
    /// Turns the state of one item's comparison into the reason it did or did not get a segment.
    /// </summary>
    /// <param name="hasConsensus">Whether a region was agreed on.</param>
    /// <param name="counterpartsCompared">How many counterparts were actually compared.</param>
    /// <param name="anySharedRegion">Whether any counterpart shared a run of audio at all.</param>
    /// <param name="candidateCount">How many counterparts produced an in-window region.</param>
    /// <returns>The outcome to record.</returns>
    internal static SegmentMatchOutcome ClassifyOutcome(
        bool hasConsensus,
        int counterpartsCompared,
        bool anySharedRegion,
        int candidateCount)
    {
        if (hasConsensus)
        {
            return SegmentMatchOutcome.Matched;
        }

        if (counterpartsCompared == 0)
        {
            return SegmentMatchOutcome.NoComparableCounterparts;
        }

        if (!anySharedRegion)
        {
            return SegmentMatchOutcome.NoSharedAudio;
        }

        // Shared audio existed, so the loss happened either in the window filters or in the vote.
        return candidateCount == 0
            ? SegmentMatchOutcome.OutsideWindow
            : SegmentMatchOutcome.NoConsensus;
    }

    /// <summary>
    /// Orders a group's fingerprints by episode number so index distance in the list is a proxy
    /// for broadcast adjacency. Items that are not episodes (or carry no index) sort last, in a
    /// stable order, so they never displace real episodes from each other's neighbourhoods.
    /// </summary>
    private List<ChromaprintResult> OrderByEpisode(List<ChromaprintResult> fingerprints)
    {
        return fingerprints
            .OrderBy(f => (_libraryManager.GetItemById(f.ItemId) as Episode)?.IndexNumber ?? int.MaxValue)
            .ThenBy(f => f.ItemId)
            .ToList();
    }

    /// <summary>
    /// Enumerates every index of a list other than <paramref name="index"/>, nearest first,
    /// alternating below/above so both directions are sampled evenly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counterparts used to be taken as simply the first <see cref="MaxCounterpartsPerItem"/> rows
    /// of the group. A season whose opening changes partway through - split-cour anime, a
    /// mid-season rebrand - contains two disjoint sets of episodes that share an OP, and under
    /// that rule every episode past the eighth was only ever compared against the head of the
    /// season. The only audio the two halves have in common is whatever is glued to the front of
    /// every file (a distributor ident), so the second half's intro collapsed onto that instead of
    /// its actual opening.
    /// </para>
    /// <para>
    /// Walking outwards from each item keeps the comparison inside the run of episodes most likely
    /// to share an opening, and costs nothing extra: the same number of pairs are compared.
    /// </para>
    /// </remarks>
    /// <param name="index">The index to walk outwards from.</param>
    /// <param name="count">The number of items in the list.</param>
    /// <returns>The other indices, in ascending order of distance from <paramref name="index"/>.</returns>
    internal static IEnumerable<int> NeighboursByDistance(int index, int count)
    {
        for (int distance = 1; distance < count; distance++)
        {
            var before = index - distance;
            if (before >= 0)
            {
                yield return before;
            }

            var after = index + distance;
            if (after < count)
            {
                yield return after;
            }
        }
    }

    /// <summary>
    /// Picks the region that the most counterparts agree on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candidates are clustered by start position within <see cref="ConsensusToleranceTicks"/>
    /// and the largest cluster wins, represented by its median start and end so one outlier
    /// inside an otherwise-agreeing cluster cannot stretch the boundary.
    /// </para>
    /// <para>
    /// When there are at least two comparable counterparts a region must be corroborated by two
    /// of them; a single agreeing counterpart is accepted only when that is all there is (a
    /// two-episode season). This is what stops one spurious pairing from defining an item's
    /// intro. Equally-supported clusters are separated by length and then by earliest start, so
    /// the result never depends on row order.
    /// </para>
    /// </remarks>
    /// <param name="candidates">One candidate region per counterpart, in absolute ticks.</param>
    /// <param name="counterpartsCompared">How many counterparts were actually compared.</param>
    /// <returns>The agreed region, or <c>null</c> when nothing reaches the required support.</returns>
    internal static (long StartTicks, long EndTicks)? SelectConsensusRegion(
        IReadOnlyList<(long StartTicks, long EndTicks)> candidates,
        int counterpartsCompared)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return null;
        }

        var requiredVotes = counterpartsCompared >= 2 ? 2 : 1;
        if (candidates.Count < requiredVotes)
        {
            return null;
        }

        var ordered = candidates.OrderBy(c => c.StartTicks).ThenBy(c => c.EndTicks).ToList();

        List<(long StartTicks, long EndTicks)>? best = null;
        var current = new List<(long StartTicks, long EndTicks)> { ordered[0] };

        for (int i = 1; i <= ordered.Count; i++)
        {
            if (i < ordered.Count && ordered[i].StartTicks - current[0].StartTicks <= ConsensusToleranceTicks)
            {
                current.Add(ordered[i]);
                continue;
            }

            if (best is null || IsBetterCluster(current, best))
            {
                best = current;
            }

            if (i < ordered.Count)
            {
                current = [ordered[i]];
            }
        }

        if (best is null || best.Count < requiredVotes)
        {
            return null;
        }

        return (Median(best.Select(c => c.StartTicks)), Median(best.Select(c => c.EndTicks)));
    }

    /// <summary>
    /// Ranks two candidate clusters: more corroboration first, then the longer region.
    /// </summary>
    /// <remarks>
    /// At a mid-season opening change the counterparts of an episode sitting on the boundary split
    /// evenly between the old opening and the new one, so cluster size alone cannot decide. The
    /// clusters are not equally informative though: one of them is the real opening (a minute or
    /// more) and the other is whatever short ident every file in the season begins with. Falling
    /// back to earliest start would hand the segment to the ident. Only a strict improvement
    /// replaces the incumbent, so a genuine tie still resolves to the earliest cluster and the
    /// choice stays independent of enumeration order.
    /// </remarks>
    private static bool IsBetterCluster(
        List<(long StartTicks, long EndTicks)> candidate,
        List<(long StartTicks, long EndTicks)> incumbent)
    {
        if (candidate.Count != incumbent.Count)
        {
            return candidate.Count > incumbent.Count;
        }

        return ClusterDurationTicks(candidate) > ClusterDurationTicks(incumbent);
    }

    /// <summary>
    /// The duration of the region a cluster represents, measured on the same medians that
    /// <see cref="SelectConsensusRegion"/> returns so ranking and result can never disagree.
    /// </summary>
    private static long ClusterDurationTicks(List<(long StartTicks, long EndTicks)> cluster)
    {
        return Median(cluster.Select(c => c.EndTicks)) - Median(cluster.Select(c => c.StartTicks));
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Drops fingerprints whose owning item no longer exists in the library.
    /// Without this, a ghost fingerprint left over from a silent re-ID.
    /// </summary>
    private List<ChromaprintResult> FilterOutOrphans(List<ChromaprintResult> fingerprints, Guid groupId, string region)
    {
        var kept = fingerprints.Where(f => _libraryManager.GetItemById(f.ItemId) is not null).ToList();
        var dropped = fingerprints.Count - kept.Count;
        if (dropped > 0)
        {
            _logger.LogWarning(
                "Chromaprint: dropped {Dropped} orphan {Region} fingerprint(s) from group {GroupId} - run AnalyzeSegmentsTask to purge them from the DB",
                dropped,
                region,
                groupId);
        }

        return kept;
    }
}
