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
using MediaBrowser.Controller.Entities.Movies;
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
/// Detects intro/outro segments using ffmpeg black frame detection.
/// Only scans the intro region (start) and outro region (end) of the video,
/// not the entire file.
/// </summary>
public class BlackFrameProvider : IMediaSegmentProvider, IHasOrder
{
    /// <summary>
    /// Gap between two black stretches that still counts as one credit run. Sized for the
    /// interruptions credits contain - a distributor logo, a localisation slate.
    /// </summary>
    internal const double DenseRegionMergeGapSeconds = 20;

    /// <summary>
    /// Fraction of a candidate region that has to be black for it to read as roll credits.
    /// </summary>
    internal const double DenseRegionMinimumCoverage = 0.50;

    private readonly FfmpegBlackFrameService _blackFrameService;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly RefinementPipeline _refinementPipeline;
    private readonly ILogger<BlackFrameProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlackFrameProvider"/> class.
    /// </summary>
    /// <param name="blackFrameService">The black frame service.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="refinementPipeline">The refinement pipeline.</param>
    /// <param name="logger">The logger.</param>
    public BlackFrameProvider(
        FfmpegBlackFrameService blackFrameService,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        RefinementPipeline refinementPipeline,
        ILogger<BlackFrameProvider> logger)
    {
        _blackFrameService = blackFrameService;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _dbContextFactory = dbContextFactory;
        _refinementPipeline = refinementPipeline;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ProviderNames.BlackFrame;

    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return ValueTask.FromResult(config.EnableBlackFrameProvider && item is Video);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableBlackFrameProvider)
        {
            return [];
        }

        try
        {
            using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var existingStatus = await db.AnalysisStatuses
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ItemId == request.ItemId && s.ProviderName == Name, cancellationToken)
                .ConfigureAwait(false);

            if (existingStatus is null || !existingStatus.HasResults)
            {
                return [];
            }

            // Serve the boundaries that analysis actually settled on.
            var stored = await db.ChapterAnalysisResults
                .AsNoTracking()
                .Where(r => r.ItemId == request.ItemId
                    && SegmentSourceNames.BlackFrameOwned.Contains(r.MatchedChapterName))
                .OrderBy(r => r.StartTicks)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (stored.Count > 0)
            {
                return stored.Select(r => new MediaSegmentDto
                {
                    ItemId = r.ItemId,
                    Type = (MediaSegmentType)r.SegmentType,
                    StartTicks = r.StartTicks,
                    EndTicks = r.EndTicks
                }).ToList();
            }

            // Fallback for rows written before refined boundaries were persisted: re-cluster the
            // cached samples so an upgraded install keeps serving segments until the next
            // analysis run replaces them with refined ones.
            return await BuildSegmentsFromCachedFrames(db, request.ItemId, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Host is shutting down - the DbContextFactory's underlying service provider has been
            // disposed. Return empty rather than letting MediaSegmentManager log this as a failure.
            return [];
        }
    }

    /// <summary>
    /// Analyzes an item for black frames and stores results in the database.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AnalyzeAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item?.Path is null || (item.RunTimeTicks ?? 0) <= 0)
        {
            using var dbEmpty = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await AnalysisStatusWriter.UpsertAsync(
                dbEmpty,
                itemId,
                Name,
                hasResults: false,
                ConfigHasher.BlackFrameSegments(Plugin.Instance?.Configuration ?? new PluginConfiguration()),
                cancellationToken).ConfigureAwait(false);

            await dbEmpty.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var extractionHash = ConfigHasher.BlackFrameExtraction(config);
        var runtimeSeconds = item.RunTimeTicks!.Value / (double)TimeSpan.TicksPerSecond;

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var nowUtc = DateTime.UtcNow;

        // Re-analysis must be idempotent: a recalculation with clearCache=false reaches here
        // without a preceding cleanup, and the frame rows below would otherwise collide on
        // their (ItemId, TimestampTicks) key.
        await db.BlackFrameResults
            .Where(r => r.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // Get the video stream for hardware acceleration eligibility and resolution
        var videoStream = _mediaSourceManager.GetMediaStreams(itemId)
            .FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var videoCodec = videoStream?.Codec;
        var sourceHeight = videoStream?.Height ?? 0;

        // Detect letterboxing (cached per item)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var crop = await GetOrDetectCropAsync(db, itemId, item.Path, runtimeSeconds, videoCodec, cancellationToken).ConfigureAwait(false);
        var cropTime = sw.Elapsed;

        // Scan intro region. Both scans run with amount=0 so ffmpeg reports every frame: the
        // darkness distribution is what SelectBlackFrames normalizes the configured percentage
        // against, and it is gone once ffmpeg has filtered on our behalf.
        var introScanSeconds = IntroScanSeconds(runtimeSeconds, config);
        sw.Restart();
        var introScan = await _blackFrameService.DetectBlackFramesAsync(
            item.Path, 0, 0, introScanSeconds, crop, sourceHeight, config.BlackFrameAnalysisHeight, videoCodec, cancellationToken).ConfigureAwait(false);
        var introTime = sw.Elapsed;

        // Scan outro region
        var outroStartSeconds = OutroScanStartSeconds(runtimeSeconds, config);
        var outroScanSeconds = runtimeSeconds - outroStartSeconds;
        sw.Restart();
        var outroScan = await _blackFrameService.DetectBlackFramesAsync(
            item.Path, 0, outroStartSeconds, outroScanSeconds, crop, sourceHeight, config.BlackFrameAnalysisHeight, videoCodec, cancellationToken).ConfigureAwait(false);
        var outroTime = sw.Elapsed;

        // Each region is normalized against its own distribution: an intro that opens on a bright
        // title card and credits that run dark end to end have nothing to say about each other.
        var introFrames = SelectBlackFrames(introScan, config.BlackFrameMinimumPercentage);
        var outroFrames = SelectBlackFrames(outroScan, config.BlackFrameMinimumPercentage);

        // Deduplicate frames by TimestampTicks (ffmpeg can report duplicates,
        // and intro/outro scan regions can overlap for short files).
        var seenTimestamps = new HashSet<long>();
        var frameRows = new List<BlackFrameResult>(introFrames.Count + outroFrames.Count);
        foreach (var (timestampTicks, blackPercentage) in introFrames.Concat(outroFrames))
        {
            if (seenTimestamps.Add(timestampTicks))
            {
                frameRows.Add(new BlackFrameResult
                {
                    ItemId = itemId,
                    TimestampTicks = timestampTicks,
                    BlackPercentage = blackPercentage,
                    ConfigHash = extractionHash,
                    CreatedAt = nowUtc
                });
            }
        }

        if (frameRows.Count > 0)
        {
            db.BlackFrameResults.AddRange(frameRows);
        }

        var segmentCount = await BuildAndPersistSegmentsAsync(
            db, item, config, introFrames, outroFrames, videoCodec, nowUtc, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "BlackFrame: found {SegmentCount} segments for \"{ItemName}\" ({Path}) - {IntroFrames} intro frames, {OutroFrames} outro frames (crop={CropMs}ms, intro={IntroMs}ms/{IntroScan:F0}s, outro={OutroMs}ms/{OutroScan:F0}s)",
            segmentCount,
            item.Name,
            item.Path,
            introFrames.Count,
            outroFrames.Count,
            (long)cropTime.TotalMilliseconds,
            (long)introTime.TotalMilliseconds,
            introScanSeconds,
            (long)outroTime.TotalMilliseconds,
            outroScanSeconds);
    }

    /// <summary>
    /// Recomputes the item's segments from already-cached black-frame samples, without re-running
    /// the (expensive) ffmpeg scan.
    /// </summary>
    /// <remarks>
    /// The clustering thresholds and duration windows are cheap to re-apply but the extraction is
    /// not, so they are tracked by a separate config hash. When only the cheap half changed, the
    /// scheduled task calls this instead of <see cref="AnalyzeAsync"/>. This preserves the
    /// "tweak a threshold and see it take effect" behaviour that the old serve-time re-clustering
    /// provided, while still letting refined boundaries be the thing that gets served.
    /// </remarks>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RebuildSegmentsFromCacheAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item?.Path is null || (item.RunTimeTicks ?? 0) <= 0)
        {
            return;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var runtimeSeconds = item.RunTimeTicks!.Value / (double)TimeSpan.TicksPerSecond;

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var frames = await db.BlackFrameResults
            .AsNoTracking()
            .Where(r => r.ItemId == itemId)
            .OrderBy(r => r.TimestampTicks)
            .Select(r => new { r.TimestampTicks, r.BlackPercentage })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Partition the cached samples back into the two scan windows the extraction used, so
        // clustering sees exactly what it would have seen during a full run. The windows can
        // overlap on short files - as they do during extraction - so a frame may appear in both.
        var introCutoffTicks = (long)(IntroScanSeconds(runtimeSeconds, config) * TimeSpan.TicksPerSecond);
        var outroStartTicks = (long)(OutroScanStartSeconds(runtimeSeconds, config) * TimeSpan.TicksPerSecond);

        var introFrames = frames.Where(f => f.TimestampTicks <= introCutoffTicks)
            .Select(f => (f.TimestampTicks, f.BlackPercentage)).ToList();
        var outroFrames = frames.Where(f => f.TimestampTicks >= outroStartTicks)
            .Select(f => (f.TimestampTicks, f.BlackPercentage)).ToList();

        var videoCodec = _mediaSourceManager.GetMediaStreams(itemId)
            .FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Codec;

        var segmentCount = await BuildAndPersistSegmentsAsync(
            db, item, config, introFrames, outroFrames, videoCodec, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "BlackFrame: rebuilt {SegmentCount} segments from {FrameCount} cached frames for \"{ItemName}\"",
            segmentCount,
            frames.Count,
            item.Name);
    }

    /// <summary>
    /// Clusters the given frames, selects the best intro/outro, refines the boundaries, and stages
    /// the resulting rows plus the analysis status on the context. Does not save.
    /// </summary>
    /// <returns>The number of segment rows staged.</returns>
    private async Task<int> BuildAndPersistSegmentsAsync(
        SegmentDbContext db,
        BaseItem item,
        PluginConfiguration config,
        List<(long TimestampTicks, double BlackPercentage)> introFrames,
        List<(long TimestampTicks, double BlackPercentage)> outroFrames,
        string? videoCodec,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var itemId = item.Id;
        var runtimeTicks = item.RunTimeTicks!.Value;
        var segmentHash = ConfigHasher.BlackFrameSegments(config);
        var segments = new List<ChapterAnalysisResult>();

        // Clear prior rows so both a re-analysis and a cache rebuild are idempotent against the
        // unique (ItemId, SegmentType, MatchedChapterName, StartTicks) index.
        await db.ChapterAnalysisResults
            .Where(r => r.ItemId == itemId
                && SegmentSourceNames.BlackFrameOwned.Contains(r.MatchedChapterName))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        var minClusterTicks = config.BlackFrameMinDurationMs * TimeSpan.TicksPerMillisecond;

        var introSegment = FindBestIntroCluster(itemId, ClusterFrames(introFrames, minClusterTicks), config);
        if (introSegment is not null)
        {
            var (refinedStart, refinedEnd) = await _refinementPipeline.RefineAsync(
                itemId,
                introSegment.StartTicks,
                introSegment.EndTicks,
                item.Path,
                videoCodec,
                cancellationToken,
                config.MinIntroDurationSeconds).ConfigureAwait(false);

            segments.Add(new ChapterAnalysisResult
            {
                ItemId = itemId,
                SegmentType = (int)MediaSegmentType.Intro,
                StartTicks = refinedStart,
                EndTicks = refinedEnd,
                MatchedChapterName = SegmentSourceNames.BlackFrameIntro,
                ConfigHash = segmentHash,
                CreatedAt = nowUtc
            });
        }

        var outroSegment = FindBestOutroCluster(
            itemId,
            ClusterFrames(outroFrames, minClusterTicks),
            runtimeTicks,
            config,
            item is Movie,
            [.. outroFrames.Select(f => f.TimestampTicks)]);
        if (outroSegment is not null)
        {
            var (outroRefinedStart, outroRefinedEnd) = await _refinementPipeline.RefineAsync(
                itemId,
                outroSegment.StartTicks,
                outroSegment.EndTicks,
                item.Path,
                videoCodec,
                cancellationToken,
                config.MinOutroDurationSeconds).ConfigureAwait(false);

            // A trailing gap shorter than MinPreviewDurationSeconds is black/silence before EOF
            // rather than a teaser; absorb it so the outro runs to the end instead of leaving a
            // misleading one-second Preview. Mirrors ChromaprintProvider's handling.
            if (config.EnablePreviewInference
                && outroRefinedEnd < runtimeTicks
                && (runtimeTicks - outroRefinedEnd) / (double)TimeSpan.TicksPerSecond < config.MinPreviewDurationSeconds)
            {
                outroRefinedEnd = runtimeTicks;
            }

            segments.Add(new ChapterAnalysisResult
            {
                ItemId = itemId,
                SegmentType = (int)MediaSegmentType.Outro,
                StartTicks = outroRefinedStart,
                EndTicks = outroRefinedEnd,
                MatchedChapterName = SegmentSourceNames.BlackFrameOutro,
                ConfigHash = segmentHash,
                CreatedAt = nowUtc
            });

            // Anything left after the refined outro end is a preview/next-episode teaser, up to
            // the configured cap (beyond that it is more likely a post-credits scene). This is
            // anchored to the same refined end the outro row uses, so the two never disagree.
            if (config.EnablePreviewInference && outroRefinedEnd < runtimeTicks)
            {
                var previewDurationSeconds = (runtimeTicks - outroRefinedEnd) / (double)TimeSpan.TicksPerSecond;
                if (previewDurationSeconds <= config.MaxPreviewDurationSeconds)
                {
                    _logger.LogDebug(
                        "Detected {Duration:F1}s preview after outro for \"{ItemName}\"",
                        previewDurationSeconds,
                        item.Name);

                    segments.Add(new ChapterAnalysisResult
                    {
                        ItemId = itemId,
                        SegmentType = (int)MediaSegmentType.Preview,
                        StartTicks = outroRefinedEnd,
                        EndTicks = runtimeTicks,
                        MatchedChapterName = SegmentSourceNames.BlackFramePreview,
                        ConfigHash = segmentHash,
                        CreatedAt = nowUtc
                    });
                }
            }
        }

        // Persist the refined boundaries. These - not the raw frame samples - are what
        // GetMediaSegments serves, so every silence/chapter/keyframe adjustment computed above
        // actually reaches the player.
        db.ChapterAnalysisResults.AddRange(segments);

        await AnalysisStatusWriter.UpsertAsync(
            db,
            itemId,
            Name,
            segments.Count > 0,
            segmentHash,
            cancellationToken).ConfigureAwait(false);

        return segments.Count;
    }

    /// <summary>
    /// Length of the leading region scanned for intro black frames, in seconds.
    /// </summary>
    /// <param name="runtimeSeconds">The item runtime in seconds.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The scan length in seconds.</returns>
    internal static double IntroScanSeconds(double runtimeSeconds, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Math.Min(runtimeSeconds * ChromaprintRegions.IntroFraction, config.MaxIntroDurationSeconds * 2.0);
    }

    /// <summary>
    /// Start of the trailing region scanned for outro black frames, in seconds.
    /// </summary>
    /// <param name="runtimeSeconds">The item runtime in seconds.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The scan start offset in seconds.</returns>
    internal static double OutroScanStartSeconds(double runtimeSeconds, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Math.Max(0, runtimeSeconds - config.OutroAnalysisSeconds);
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

        // ExecuteDeleteAsync issues a single SQL DELETE without materializing or tracking
        // entities; wrapping the four deletes in a transaction keeps the cleanup atomic so
        // a partial failure can't leave orphaned rows referencing this item.
        using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await db.BlackFrameResults
            .Where(r => r.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.CropDetectResults
            .Where(r => r.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.ChapterAnalysisResults
            .Where(r => r.ItemId == itemId
                && SegmentSourceNames.BlackFrameOwned.Contains(r.MatchedChapterName))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.AnalysisStatuses
            .Where(s => s.ItemId == itemId && s.ProviderName == Name)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int Width, int Height, int X, int Y)?> GetOrDetectCropAsync(
        SegmentDbContext db,
        Guid itemId,
        string filePath,
        double runtimeSeconds,
        string? videoCodec,
        CancellationToken cancellationToken)
    {
        // Check for cached crop result
        var cached = await db.CropDetectResults
            .FirstOrDefaultAsync(r => r.ItemId == itemId, cancellationToken)
            .ConfigureAwait(false);

        if (cached is not null)
        {
            // CropWidth=0 means "no letterboxing" (sentinel value)
            if (cached.CropWidth == 0)
            {
                return null;
            }

            return (cached.CropWidth, cached.CropHeight, cached.CropX, cached.CropY);
        }

        // Run crop detection
        var crop = await _blackFrameService.DetectCropAsync(filePath, runtimeSeconds, videoCodec, cancellationToken).ConfigureAwait(false);

        // Cache the result (store 0,0,0,0 as sentinel for "no letterboxing")
        db.CropDetectResults.Add(new CropDetectResult
        {
            ItemId = itemId,
            CropWidth = crop?.Width ?? 0,
            CropHeight = crop?.Height ?? 0,
            CropX = crop?.X ?? 0,
            CropY = crop?.Y ?? 0,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return crop;
    }

    internal static MediaSegmentDto? FindBestIntroCluster(
        Guid itemId,
        List<(long Start, long End)> clusters,
        PluginConfiguration config)
    {
        var maxIntroTicks = config.MaxIntroDurationSeconds * TimeSpan.TicksPerSecond;
        var minIntroTicks = config.MinIntroDurationSeconds * TimeSpan.TicksPerSecond;

        (long Start, long End)? bestCluster = null;
        foreach (var cluster in clusters.Where(c => c.End <= maxIntroTicks && c.End >= minIntroTicks))
        {
            bestCluster = cluster;
        }

        if (bestCluster is null)
        {
            return null;
        }

        return new MediaSegmentDto
        {
            ItemId = itemId,
            Type = MediaSegmentType.Intro,
            StartTicks = 0,
            EndTicks = bestCluster.Value.End
        };
    }

    internal static MediaSegmentDto? FindBestOutroCluster(
        Guid itemId,
        List<(long Start, long End)> clusters,
        long runtimeTicks,
        PluginConfiguration config,
        bool isMovie = false,
        IReadOnlyList<long>? blackTicks = null)
    {
        var minOutroTicks = config.MinOutroDurationSeconds * TimeSpan.TicksPerSecond;
        var maxOutro = isMovie ? config.MaxMovieOutroDurationSeconds : config.MaxOutroDurationSeconds;
        var maxOutroTicks = maxOutro * TimeSpan.TicksPerSecond;

        // Roll credits are a sustained black region, not a fade, and beat the cluster scan below:
        // interruptions break the credits into pieces, and that scan then keeps the last piece,
        // starting the outro well inside the credits rather than at them.
        var denseStart = FindDenseBlackRegionStart(blackTicks, runtimeTicks, config);
        if (denseStart is not null)
        {
            var denseFromEnd = runtimeTicks - denseStart.Value;
            if (denseFromEnd >= minOutroTicks && denseFromEnd <= maxOutroTicks)
            {
                return new MediaSegmentDto
                {
                    ItemId = itemId,
                    Type = MediaSegmentType.Outro,
                    StartTicks = denseStart.Value,
                    EndTicks = runtimeTicks
                };
            }
        }

        // Clusters are ordered by start-ticks ascending. Keep the last one that
        // still satisfies the outro window so we pick the latest fade-to-black that
        // isn't too close to the very end. Mirrors the logic in FindBestIntroCluster.
        // Credits that are not black at all - an ending over artwork - have no sustained region,
        // only the fade that leads into them, so this stays the fallback.
        (long Start, long End)? bestCluster = null;
        foreach (var cluster in clusters)
        {
            var durationFromEnd = runtimeTicks - cluster.Start;
            if (durationFromEnd >= minOutroTicks && durationFromEnd <= maxOutroTicks)
            {
                bestCluster = cluster;
            }
        }

        if (bestCluster is null)
        {
            return null;
        }

        return new MediaSegmentDto
        {
            ItemId = itemId,
            Type = MediaSegmentType.Outro,
            StartTicks = bestCluster.Value.Start,
            EndTicks = runtimeTicks
        };
    }

    /// <summary>
    /// Finds where the latest sustained run of black frames in the outro scan region begins.
    /// </summary>
    /// <remarks>
    /// Only the frames that counted as black are stored, so coverage cannot be measured against a
    /// total frame count. The interval is recovered from the black frames instead - inside a black
    /// stretch they are consecutive, so the smallest gaps present are the frame interval - which is
    /// what makes the analysis pass and a rebuild from cache agree.
    /// </remarks>
    /// <param name="blackTicks">Timestamps of the frames that counted as black, or null when unavailable.</param>
    /// <param name="runtimeTicks">The item runtime.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The start of the latest qualifying region, or null when there is none.</returns>
    internal static long? FindDenseBlackRegionStart(
        IReadOnlyList<long>? blackTicks,
        long runtimeTicks,
        PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (blackTicks is null)
        {
            return null;
        }

        var runtimeSeconds = runtimeTicks / (double)TimeSpan.TicksPerSecond;
        var regionStartTicks = (long)(OutroScanStartSeconds(runtimeSeconds, config) * TimeSpan.TicksPerSecond);
        var ticks = blackTicks.Where(t => t >= regionStartTicks).Order().ToList();

        if (ticks.Count < 3)
        {
            return null;
        }

        var gaps = new List<long>(ticks.Count - 1);
        for (var i = 1; i < ticks.Count; i++)
        {
            gaps.Add(ticks[i] - ticks[i - 1]);
        }

        gaps.Sort();

        // Only the lower half is in-stretch spacing; the jumps between stretches would drag a
        // plain median up towards a scene length.
        var sampleCount = Math.Max(3, gaps.Count / 2);
        var stepTicks = gaps[Math.Min(sampleCount, gaps.Count) / 2];
        if (stepTicks <= 0)
        {
            return null;
        }

        var mergeGapTicks = (long)(DenseRegionMergeGapSeconds * TimeSpan.TicksPerSecond);
        var minDurationTicks = config.MinOutroDurationSeconds * TimeSpan.TicksPerSecond;

        long? best = null;
        var runStart = ticks[0];
        var runEnd = ticks[0];
        var runCount = 1;

        void Consider(long start, long end, int count)
        {
            if (end - start < minDurationTicks)
            {
                return;
            }

            if (count * (double)stepTicks / (end - start) < DenseRegionMinimumCoverage)
            {
                return;
            }

            best = start;
        }

        for (var i = 1; i < ticks.Count; i++)
        {
            if (ticks[i] - runEnd <= mergeGapTicks)
            {
                runEnd = ticks[i];
                runCount++;
                continue;
            }

            Consider(runStart, runEnd, runCount);
            runStart = ticks[i];
            runEnd = ticks[i];
            runCount = 1;
        }

        Consider(runStart, runEnd, runCount);
        return best;
    }

    private async Task<IReadOnlyList<MediaSegmentDto>> BuildSegmentsFromCachedFrames(
        SegmentDbContext db,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        var runtimeTicks = item?.RunTimeTicks ?? 0;
        if (runtimeTicks <= 0)
        {
            return [];
        }

        var frames = await db.BlackFrameResults
            .AsNoTracking()
            .Where(r => r.ItemId == itemId)
            .OrderBy(r => r.TimestampTicks)
            .Select(r => new { r.TimestampTicks, r.BlackPercentage })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var minDurationTicks = config.BlackFrameMinDurationMs * TimeSpan.TicksPerMillisecond;
        var allClusters = ClusterFrames(
            frames.Select(f => (f.TimestampTicks, f.BlackPercentage)).ToList(),
            minDurationTicks);

        var segments = new List<MediaSegmentDto>();

        var introSegment = FindBestIntroCluster(itemId, allClusters, config);
        if (introSegment is not null)
        {
            segments.Add(introSegment);
        }

        var outroSegment = FindBestOutroCluster(
            itemId,
            allClusters,
            runtimeTicks,
            config,
            item is Movie,
            [.. frames.Select(f => f.TimestampTicks)]);
        if (outroSegment is not null)
        {
            segments.Add(outroSegment);
        }

        return segments;
    }

    /// <summary>
    /// Keeps the frames of an unfiltered scan that count as black, with the configured percentage
    /// normalized against the darkness the scan actually contains.
    /// </summary>
    /// <remarks>
    /// Only the survivors are stored, so the samples in the database mean what they always did - a
    /// frame that qualified as black. What changed is that "qualified" now accounts for material
    /// that never reaches full brightness, where a fixed bar lets ordinary dark scenes pass as a
    /// transition.
    /// </remarks>
    /// <param name="scan">Every frame ffmpeg reported for the region.</param>
    /// <param name="minimumPercentage">The configured minimum black percentage.</param>
    /// <returns>The frames at or above the normalized minimum, in scan order.</returns>
    internal static List<(long TimestampTicks, double BlackPercentage)> SelectBlackFrames(
        List<(long TimestampTicks, double BlackPercentage)> scan,
        double minimumPercentage)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.Count == 0)
        {
            return [];
        }

        var minimum = BlackFrameThresholdHelper.NormalizeThreshold(
            [.. scan.Select(f => f.BlackPercentage)],
            minimumPercentage);

        return [.. scan.Where(f => f.BlackPercentage >= minimum)];
    }

    private static List<(long Start, long End)> ClusterFrames(
        List<(long TimestampTicks, double BlackPercentage)> frames,
        long minDurationTicks)
    {
        var clusters = new List<(long Start, long End)>();
        if (frames.Count == 0)
        {
            return clusters;
        }

        var maxGap = TimeSpan.TicksPerSecond;
        var clusterStart = frames[0].TimestampTicks;
        var clusterEnd = frames[0].TimestampTicks;

        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].TimestampTicks - clusterEnd <= maxGap)
            {
                clusterEnd = frames[i].TimestampTicks;
            }
            else
            {
                if (clusterEnd - clusterStart >= minDurationTicks)
                {
                    clusters.Add((clusterStart, clusterEnd));
                }

                clusterStart = frames[i].TimestampTicks;
                clusterEnd = frames[i].TimestampTicks;
            }
        }

        if (clusterEnd - clusterStart >= minDurationTicks)
        {
            clusters.Add((clusterStart, clusterEnd));
        }

        return clusters;
    }
}
