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
    /// How many counterparts an item is compared against. Enough for a majority vote, and bounds
    /// an otherwise O(N²) pass at O(N). Which ones matters as much as how many - see
    /// <see cref="NeighboursByDistance"/>.
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
    /// The same tolerance for outros, measured back from the end. Much tighter than the intro's:
    /// credits length is fixed within a season where cold-open length is not, and the whole outro
    /// population sits inside the last few minutes, so 120 s would cluster everything together.
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

    /// <summary>
    /// An intro shorter than this fraction of the season's longest is treated as a mismatch worth
    /// retrying. Catches the item that locked onto a short shared ident because its real opening
    /// lay outside the first-pass region - which reports success, not failure.
    /// </summary>
    private const double SuspectIntroFraction = 0.5;

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
    /// <param name="minRegionSeconds">Widens the intro region to at least this, for a retry.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task GenerateFingerprintAsync(
        Guid itemId,
        string region,
        CancellationToken cancellationToken,
        double minRegionSeconds = 0)
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

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var isCreditsRegion = string.Equals(region, SegmentSourceNames.RegionCredits, StringComparison.Ordinal);
        var configHash = isCreditsRegion
            ? ConfigHasher.ChromaprintCredits(config)
            : ConfigHasher.ChromaprintIntro(config);

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var stored = await db.ChromaprintResults
            .Where(r => r.ItemId == itemId && r.Region == region)
            .Select(r => new { r.AnalysisDurationSeconds, r.ConfigHash })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Keep what is there when it was produced under this configuration and reaches at least as
        // far as asked. Judging both here rather than in the caller is what lets the caller stop
        // deleting the row up front to force a rebuild. A zero-width row is the sentinel written
        // when extraction yielded nothing; it is final, and comparing it on width would re-run
        // ffmpeg against an item with no usable audio on every pass.
        var covered = stored is null ? (int?)null : stored.AnalysisDurationSeconds;
        if (stored is not null
            && string.Equals(stored.ConfigHash, configHash, StringComparison.Ordinal)
            && (stored.AnalysisDurationSeconds == 0 || stored.AnalysisDurationSeconds + 1 >= minRegionSeconds))
        {
            return;
        }

        var runtimeSeconds = item.RunTimeTicks!.Value / (double)TimeSpan.TicksPerSecond;

        // For short media (≤10 minutes), fingerprint the entire file in the Intro region.
        // Skip the Credits region since it would be identical.
        var isShortMedia = runtimeSeconds <= ChromaprintRegions.ShortMediaSeconds;

        if (isShortMedia && isCreditsRegion)
        {
            _logger.LogDebug("Skipping credits fingerprint for short media ({Duration:F0}s) item {ItemId}", runtimeSeconds, itemId);

            // Store an empty sentinel so the task knows this was intentionally skipped
            // and doesn't retry on every run.
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

            await StoreAsync().ConfigureAwait(false);
            return;
        }

        double startSeconds;
        double analysisSeconds;

        if (isShortMedia)
        {
            startSeconds = 0;
            analysisSeconds = runtimeSeconds;
        }
        else if (isCreditsRegion)
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
            analysisSeconds = Math.Max(ChromaprintRegions.FirstPass(runtimeSeconds), minRegionSeconds);
        }

        var fpData = await _chromaprintService.GenerateFingerprintAsync(
            item.Path,
            config.ChromaprintSampleRate,
            startSeconds,
            analysisSeconds,
            cancellationToken).ConfigureAwait(false);

        if (fpData.Length == 0)
        {
            if (covered > 0)
            {
                _logger.LogWarning(
                    "Chromaprint {Region} fingerprinting produced no data for \"{ItemName}\" ({Path}), "
                    + "keeping the existing {Covered}s fingerprint",
                    region,
                    item.Name,
                    item.Path,
                    covered);
                return;
            }

            _logger.LogDebug("Chromaprint: fingerprinting produced no data for \"{ItemName}\" ({Path}) [{Region}]", item.Name, item.Path, region);

            // Nothing to lose, so record the sentinel so the task doesn't retry on every run.
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

            await StoreAsync().ConfigureAwait(false);
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

        await StoreAsync().ConfigureAwait(false);

        _logger.LogDebug(
            "Chromaprint: generated {Region} fingerprint ({Bytes} bytes) for \"{ItemName}\" ({Path})",
            region,
            fpData.Length,
            item.Name,
            item.Path);

        // Replaces any existing row in one transaction. Every caller reaches here only after the
        // ffmpeg work has produced usable data, so a regeneration cannot leave the item with no
        // fingerprint: deleting up front meant a failed re-extraction destroyed a usable one.
        async Task StoreAsync()
        {
            // Delete unconditionally rather than only when a row was seen earlier. Whether one
            // existed was read before an ffmpeg run that can take minutes, and the scheduled task
            // does not take the per-item lock the Recalculate endpoint uses - so a row written
            // meanwhile turned the insert into a primary-key violation on (ItemId, Region). The
            // extra statement costs nothing next to the extraction it follows.
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await db.ChromaprintResults
                .Where(r => r.ItemId == itemId && r.Region == region)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
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
        // Where the fingerprinted audio ends, per item. Only outros need it (they are positioned
        // back from that point), and it has to cover items this run skips as up-to-date, so it is
        // built from the fingerprint rows rather than inside the evaluation loop.
        var anchorByItem = new Dictionary<Guid, long>(isCredits ? fingerprints.Count : 0);
        if (isCredits)
        {
            foreach (var fingerprint in fingerprints)
            {
                var itemRuntime = _libraryManager.GetItemById(fingerprint.ItemId)?.RunTimeTicks ?? 0;
                anchorByItem[fingerprint.ItemId] =
                    RegionOffsetTicks(fingerprint, itemRuntime, isCredits)
                    + (fingerprint.AnalysisDurationSeconds * TimeSpan.TicksPerSecond);
            }
        }

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

            var regionOffsetTicks = RegionOffsetTicks(current, runtimeTicks, isCredits);

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
                anchorByItem,
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
    /// Items in a group whose intro is worth searching for again over a wider region.
    /// </summary>
    /// <remarks>
    /// Two shapes qualify: no intro at all, and an intro far shorter than the season's longest.
    /// The second matters as much as the first - an episode whose opening lies past the first-pass
    /// region often still matches a brief shared ident at the head of the file, which looks like
    /// success. Items already fingerprinted at the retry width are excluded, so a group with
    /// genuinely no shared opening settles after one retry instead of re-extracting every run.
    /// </remarks>
    /// <param name="groupId">The group identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The items to re-fingerprint, and the region each needs.</returns>
    public async Task<IReadOnlyList<(Guid ItemId, double RegionSeconds)>> GetIntroRetryCandidatesAsync(
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var fingerprints = await db.ChromaprintResults
            .AsNoTracking()
            .Where(r => r.SeasonId == groupId && r.Region == SegmentSourceNames.RegionIntro && r.AnalysisDurationSeconds > 0)
            .Select(r => new { r.ItemId, r.AnalysisDurationSeconds })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (fingerprints.Count == 0)
        {
            return [];
        }

        var itemIds = fingerprints.Select(f => f.ItemId).ToList();
        var introLengths = (await db.ChapterAnalysisResults
            .AsNoTracking()
            .Where(r => itemIds.Contains(r.ItemId) && r.MatchedChapterName == SegmentSourceNames.ChromaprintIntro)
            .Select(r => new { r.ItemId, Length = r.EndTicks - r.StartTicks })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(r => r.ItemId, r => r.Length);

        var longest = introLengths.Count > 0 ? introLengths.Values.Max() : 0;
        var candidates = new List<(Guid, double)>();

        foreach (var fingerprint in fingerprints)
        {
            // Suspicion is decided from rows already in hand; the library lookup that follows only
            // happens for the few items that fail it, which keeps this affordable on every run.
            var suspect = !introLengths.TryGetValue(fingerprint.ItemId, out var length)
                || (longest > 0 && length < longest * SuspectIntroFraction);

            if (!suspect)
            {
                continue;
            }

            var runtimeTicks = _libraryManager.GetItemById(fingerprint.ItemId)?.RunTimeTicks ?? 0;
            if (runtimeTicks <= 0)
            {
                continue;
            }

            var retrySeconds = ChromaprintRegions.ForRetry(runtimeTicks / (double)TimeSpan.TicksPerSecond);
            if (retrySeconds > fingerprint.AnalysisDurationSeconds + 1)
            {
                candidates.Add((fingerprint.ItemId, retrySeconds));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Where a fingerprint's region starts within its file.
    /// </summary>
    /// <remarks>
    /// Stored explicitly because the credits region is anchored to the audio duration, which can be
    /// shorter than the container runtime. Rows written before that column existed carry 0, which
    /// is not a real value for credits - a credits fingerprint never starts at 0, since short media
    /// is fingerprinted whole under the intro region - so those fall back to the old derivation.
    /// </remarks>
    /// <param name="fingerprint">The fingerprint row.</param>
    /// <param name="runtimeTicks">The item's runtime, for the legacy fallback.</param>
    /// <param name="isCredits">Whether this is the credits region.</param>
    /// <returns>The region's start offset in ticks.</returns>
    private static long RegionOffsetTicks(ChromaprintResult fingerprint, long runtimeTicks, bool isCredits)
    {
        if (!isCredits || fingerprint.RegionStartTicks != 0)
        {
            return fingerprint.RegionStartTicks;
        }

        return Math.Max(0, runtimeTicks - (fingerprint.AnalysisDurationSeconds * TimeSpan.TicksPerSecond));
    }

    /// <summary>
    /// Applies <see cref="SelectSeasonOutliers"/> to the whole season and removes what it flags.
    /// </summary>
    /// <remarks>
    /// The population spans the season, not just items re-evaluated this run: judging a straggler
    /// needs the positions its siblings agreed on, most of which come from earlier rows. Items
    /// queued for deletion are excluded, since <paramref name="toAdd"/> replaces them.
    /// Intros are measured from the start and outros from the end, because that is what each is
    /// anchored to - runtimes vary by over 15 minutes within a season in the top decile, and
    /// measuring credits from the start dropped seasons with a dominant position from 96% to 57%.
    /// </remarks>
    private void PruneSeasonOutliers(
        string matchedChapterName,
        MediaSegmentType segmentType,
        bool isCredits,
        Dictionary<Guid, long> anchorByItem,
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

            // Measured back from the end of the *fingerprinted audio*, which is what the segment's
            // position was derived from. Taking the container runtime instead mixes coordinates on
            // any file whose duration outruns its audio - an MKV with over-long subtitles - and
            // inflates that item's distance until it looks isolated. On the sample library such
            // items were pruned at 16% against 1.4% for the rest.
            if (anchorByItem.TryGetValue(itemId, out var audioEndTicks))
            {
                population.Add((itemId, audioEndTicks - startTicks));
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
    /// Deliberately not distance from the season's average: seasons legitimately carry several
    /// positions, since the cold open varies. One 197-episode arc splits 141/41/15 across three,
    /// all correct, and a distance rule would have discarded 56 good intros there. What separates
    /// a format variant from noise is how many episodes independently landed on it, so this
    /// clusters positions and drops only clusters too small to be a variant. Skipped entirely
    /// unless one cluster holds a clear majority - without a dominant position there is nothing to
    /// be a straggler from, and guessing would remove as many good results as bad.
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

        // Chained against each cluster's *previous* member, not its first. Where the cold open
        // varies continuously the positions form a run rather than tight groups - one season
        // spreads its intros over 22s..292s with no consecutive gap above 93s - and anchoring on
        // the first member chops that run into pieces, leaving the tail as a singleton to be
        // pruned. Anchoring on the previous member keeps a continuum whole. Genuinely isolated
        // positions are unaffected: they are far from every member, not just the first.
        var clusters = new List<List<(Guid ItemId, long PositionTicks)>>();
        var current = new List<(Guid ItemId, long PositionTicks)> { ordered[0] };
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].PositionTicks - current[^1].PositionTicks <= toleranceTicks)
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
    /// Taking the first N rows instead meant every episode past the Nth was only ever compared
    /// against the head of the season. Where the opening changes partway through - split-cour
    /// anime, a mid-season rebrand - the two halves share only the ident at the front of every
    /// file, so the second half collapsed onto that. Walking outwards keeps comparisons inside the
    /// run of episodes likely to share an opening, for the same number of pairs.
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
