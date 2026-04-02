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
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existingStatus = await db.AnalysisStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ItemId == request.ItemId && s.ProviderName == Name, cancellationToken)
            .ConfigureAwait(false);

        if (existingStatus is null || !existingStatus.HasResults)
        {
            return [];
        }

        var segments = await BuildSegmentsFromCachedFrames(db, request.ItemId, cancellationToken).ConfigureAwait(false);

        // Also serve any preview segments inferred from the outro boundary
        var preview = await db.ChapterAnalysisResults
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.ItemId == request.ItemId && r.MatchedChapterName == SegmentSourceNames.BlackFramePreview,
                cancellationToken)
            .ConfigureAwait(false);

        if (preview is not null)
        {
            var list = new List<MediaSegmentDto>(segments)
            {
                new MediaSegmentDto
                {
                    ItemId = preview.ItemId,
                    Type = (MediaSegmentType)preview.SegmentType,
                    StartTicks = preview.StartTicks,
                    EndTicks = preview.EndTicks
                }
            };
            return list;
        }

        return segments;
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
            dbEmpty.AnalysisStatuses.Add(new AnalysisStatus
            {
                ItemId = itemId,
                ProviderName = Name,
                AnalyzedAt = DateTime.UtcNow,
                HasResults = false
            });

            await dbEmpty.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var configHash = ConfigHasher.BlackFrame(config);
        var runtimeTicks = item.RunTimeTicks!.Value;
        var runtimeSeconds = runtimeTicks / (double)TimeSpan.TicksPerSecond;

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var segments = new List<MediaSegmentDto>();

        // Get the video stream for hardware acceleration eligibility and resolution
        var videoStream = _mediaSourceManager.GetMediaStreams(itemId)
            .FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var videoCodec = videoStream?.Codec;
        var sourceHeight = videoStream?.Height ?? 0;

        // Detect letterboxing (cached per item)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var crop = await GetOrDetectCropAsync(db, itemId, item.Path, runtimeSeconds, videoCodec, cancellationToken).ConfigureAwait(false);
        var cropTime = sw.Elapsed;

        // Scan intro region
        var introScanSeconds = Math.Min(runtimeSeconds * config.IntroAnalysisPercent, config.MaxIntroDurationSeconds * 2.0);
        sw.Restart();
        var introFrames = await _blackFrameService.DetectBlackFramesAsync(
            item.Path, config.BlackFrameThreshold, 0, introScanSeconds, crop, sourceHeight, config.BlackFrameAnalysisHeight, videoCodec, cancellationToken).ConfigureAwait(false);
        var introTime = sw.Elapsed;

        var introClusters = ClusterFrames(introFrames, config.BlackFrameMinDurationMs * TimeSpan.TicksPerMillisecond);
        var introSegment = FindBestIntroCluster(itemId, introClusters, runtimeTicks, config);
        if (introSegment is not null)
        {
            var (refinedStart, refinedEnd) = await _refinementPipeline.RefineAsync(
                itemId, introSegment.StartTicks, introSegment.EndTicks, item.Path, videoCodec, cancellationToken).ConfigureAwait(false);

            introSegment.StartTicks = refinedStart;
            introSegment.EndTicks = refinedEnd;
            segments.Add(introSegment);
        }

        // Scan outro region
        var outroStartSeconds = Math.Max(0, runtimeSeconds - config.OutroAnalysisSeconds);
        var outroScanSeconds = runtimeSeconds - outroStartSeconds;
        sw.Restart();
        var outroFrames = await _blackFrameService.DetectBlackFramesAsync(
            item.Path, config.BlackFrameThreshold, outroStartSeconds, outroScanSeconds, crop, sourceHeight, config.BlackFrameAnalysisHeight, videoCodec, cancellationToken).ConfigureAwait(false);
        var outroTime = sw.Elapsed;

        // Deduplicate frames by TimestampTicks (ffmpeg can report duplicates,
        // and intro/outro scan regions can overlap for short files)
        var seenTimestamps = new HashSet<long>();
        foreach (var (timestampTicks, blackPercentage) in introFrames.Concat(outroFrames))
        {
            if (seenTimestamps.Add(timestampTicks))
            {
                db.BlackFrameResults.Add(new BlackFrameResult
                {
                    ItemId = itemId,
                    TimestampTicks = timestampTicks,
                    BlackPercentage = blackPercentage,
                    ConfigHash = configHash,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        var outroClusters = ClusterFrames(outroFrames, config.BlackFrameMinDurationMs * TimeSpan.TicksPerMillisecond);
        var outroSegment = FindBestOutroCluster(itemId, outroClusters, runtimeTicks, config, item is Movie);
        if (outroSegment is not null)
        {
            var (outroRefinedStart, outroRefinedEnd) = await _refinementPipeline.RefineAsync(
                itemId, outroSegment.StartTicks, outroSegment.EndTicks, item.Path, videoCodec, cancellationToken).ConfigureAwait(false);

            outroSegment.StartTicks = outroRefinedStart;
            outroSegment.EndTicks = outroRefinedEnd;
            segments.Add(outroSegment);

            // If the outro ends before the episode's runtime, the remaining portion
            // is likely a preview/next-episode teaser (max 30s)
            if (config.EnablePreviewInference && outroRefinedEnd < runtimeTicks)
            {
                var previewDurationSeconds = (runtimeTicks - outroRefinedEnd) / (double)TimeSpan.TicksPerSecond;
                if (previewDurationSeconds <= 30.0)
                {
                    _logger.LogDebug(
                        "Detected {Duration:F1}s preview after outro for \"{ItemName}\"",
                        previewDurationSeconds,
                        item.Name);

                    db.ChapterAnalysisResults.Add(new ChapterAnalysisResult
                    {
                        ItemId = itemId,
                        SegmentType = (int)MediaSegmentType.Preview,
                        StartTicks = outroRefinedEnd,
                        EndTicks = runtimeTicks,
                        MatchedChapterName = SegmentSourceNames.BlackFramePreview,
                        ConfigHash = configHash,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
        }

        db.AnalysisStatuses.Add(new AnalysisStatus
        {
            ItemId = itemId,
            ProviderName = Name,
            AnalyzedAt = DateTime.UtcNow,
            HasResults = segments.Count > 0
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "BlackFrame: found {SegmentCount} segments for \"{ItemName}\" ({Path}) — {IntroFrames} intro frames, {OutroFrames} outro frames (crop={CropMs}ms, intro={IntroMs}ms/{IntroScan:F0}s, outro={OutroMs}ms/{OutroScan:F0}s)",
            segments.Count,
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
    /// Removes all cached analysis data for the specified item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        db.BlackFrameResults.RemoveRange(
            db.BlackFrameResults.Where(r => r.ItemId == itemId));
        db.CropDetectResults.RemoveRange(
            db.CropDetectResults.Where(r => r.ItemId == itemId));
        db.ChapterAnalysisResults.RemoveRange(
            db.ChapterAnalysisResults.Where(r => r.ItemId == itemId && r.MatchedChapterName == SegmentSourceNames.BlackFramePreview));
        db.AnalysisStatuses.RemoveRange(
            db.AnalysisStatuses.Where(s => s.ItemId == itemId && s.ProviderName == Name));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private static MediaSegmentDto? FindBestIntroCluster(
        Guid itemId,
        List<(long Start, long End)> clusters,
        long runtimeTicks,
        PluginConfiguration config)
    {
        var maxIntroTicks = config.MaxIntroDurationSeconds * TimeSpan.TicksPerSecond;
        var minIntroTicks = config.MinIntroDurationSeconds * TimeSpan.TicksPerSecond;

        (long Start, long End)? bestCluster = null;
        foreach (var cluster in clusters)
        {
            if (cluster.End <= maxIntroTicks && cluster.End >= minIntroTicks)
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
            Type = MediaSegmentType.Intro,
            StartTicks = 0,
            EndTicks = bestCluster.Value.End
        };
    }

    private static MediaSegmentDto? FindBestOutroCluster(
        Guid itemId,
        List<(long Start, long End)> clusters,
        long runtimeTicks,
        PluginConfiguration config,
        bool isMovie = false)
    {
        var minOutroTicks = config.MinOutroDurationSeconds * TimeSpan.TicksPerSecond;
        var maxOutro = isMovie ? config.MaxMovieOutroDurationSeconds : config.MaxOutroDurationSeconds;
        var maxOutroTicks = maxOutro * TimeSpan.TicksPerSecond;

        foreach (var cluster in clusters)
        {
            var durationFromEnd = runtimeTicks - cluster.Start;
            if (durationFromEnd >= minOutroTicks && durationFromEnd <= maxOutroTicks)
            {
                return new MediaSegmentDto
                {
                    ItemId = itemId,
                    Type = MediaSegmentType.Outro,
                    StartTicks = cluster.Start,
                    EndTicks = runtimeTicks
                };
            }
        }

        return null;
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

        var introSegment = FindBestIntroCluster(itemId, allClusters, runtimeTicks, config);
        if (introSegment is not null)
        {
            segments.Add(introSegment);
        }

        var outroSegment = FindBestOutroCluster(itemId, allClusters, runtimeTicks, config, item is Movie);
        if (outroSegment is not null)
        {
            segments.Add(outroSegment);
        }

        return segments;
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
