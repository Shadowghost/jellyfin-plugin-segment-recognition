using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BitFaster.Caching.Lru;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Refines segment boundaries by snapping to nearby silence gaps.
/// Uses asymmetric search windows: a larger inward window (toward segment center) and
/// a smaller outward window (away from segment center), reflecting the likelihood that
/// detected boundaries slightly overshoot the actual segment.
/// </summary>
public class SegmentRefiner
{
    /// <summary>
    /// Capacity of the in-process silence-interval cache.
    /// </summary>
    private const int SilenceCacheCapacity = 1024;

    private readonly FfmpegBlackFrameService _blackFrameService;
    private readonly ILogger<SegmentRefiner> _logger;

    /// <summary>
    /// LRU cache of silence-detect results keyed by every parameter that affects output.
    /// </summary>
    private readonly FastConcurrentLru<SilenceCacheKey, IReadOnlyList<(double Start, double End)>> _silenceCache
        = new(SilenceCacheCapacity);

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentRefiner"/> class.
    /// </summary>
    /// <param name="blackFrameService">The black frame service (provides silence detection).</param>
    /// <param name="logger">The logger.</param>
    public SegmentRefiner(
        FfmpegBlackFrameService blackFrameService,
        ILogger<SegmentRefiner> logger)
    {
        _blackFrameService = blackFrameService;
        _logger = logger;
    }

    /// <summary>
    /// Refines a segment's start and end ticks by snapping boundaries to nearby silence gaps.
    /// Uses asymmetric windows: for the start boundary, searches further inward (later) than
    /// outward (earlier); for the end boundary, searches further inward (earlier) than outward (later).
    /// </summary>
    /// <param name="startTicks">The original start position in ticks.</param>
    /// <param name="endTicks">The original end position in ticks.</param>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="videoCodec">The video codec name (unused, reserved for future use).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of refined (StartTicks, EndTicks).</returns>
    public async Task<(long StartTicks, long EndTicks)> RefineSegmentAsync(
        long startTicks,
        long endTicks,
        string filePath,
        string? videoCodec,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (!config.EnableSilenceRefinement)
        {
            return (startTicks, endTicks);
        }

        var inwardSeconds = config.SilenceSnapInwardSeconds;
        var outwardSeconds = config.SilenceSnapOutwardSeconds;
        var noisedB = config.SilenceDetectNoisedB;
        var minDuration = config.SilenceDetectMinDurationSeconds;

        // Kick off both boundary snaps concurrently. Each does at most one ffmpeg silencedetect invocation.
        var startTask = SnapBoundaryAsync(
            startTicks, filePath, outwardSeconds, inwardSeconds, noisedB, minDuration, cancellationToken);
        var endTask = SnapBoundaryAsync(
            endTicks, filePath, inwardSeconds, outwardSeconds, noisedB, minDuration, cancellationToken);

        await Task.WhenAll(startTask, endTask).ConfigureAwait(false);

        var refinedStart = await startTask.ConfigureAwait(false);
        var refinedEnd = await endTask.ConfigureAwait(false);

        // Ensure start < end after refinement
        if (refinedStart >= refinedEnd)
        {
            _logger.LogDebug(
                "Silence refinement would produce invalid range ({Start}-{End}), keeping original",
                refinedStart,
                refinedEnd);
            return (startTicks, endTicks);
        }

        if (refinedStart != startTicks || refinedEnd != endTicks)
        {
            _logger.LogDebug(
                "Silence refinement adjusted segment: {OldStart}-{OldEnd} -> {NewStart}-{NewEnd}",
                startTicks,
                endTicks,
                refinedStart,
                refinedEnd);
        }

        return (refinedStart, refinedEnd);
    }

    private async Task<long> SnapBoundaryAsync(
        long targetTicks,
        string filePath,
        double beforeSeconds,
        double afterSeconds,
        int noisedB,
        double minDuration,
        CancellationToken cancellationToken)
    {
        var targetSeconds = targetTicks / (double)TimeSpan.TicksPerSecond;
        var scanStart = Math.Max(0, targetSeconds - beforeSeconds);
        var scanDuration = beforeSeconds + afterSeconds;

        IReadOnlyList<(double Start, double End)> silenceIntervals;
        try
        {
            silenceIntervals = await GetOrDetectSilenceAsync(
                filePath, scanStart, scanDuration, noisedB, minDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Silence detection failed for boundary snap at {Target}s, keeping original", targetSeconds);
            return targetTicks;
        }

        if (silenceIntervals.Count == 0)
        {
            return targetTicks;
        }

        // Find the silence gap midpoint closest to the target within the asymmetric window
        var bestDistance = double.MaxValue;
        var bestMidpoint = targetSeconds;

        foreach (var (startSec, endSec) in silenceIntervals)
        {
            var midpoint = (startSec + endSec) / 2.0;
            var delta = midpoint - targetSeconds;

            // Check asymmetric bounds: negative delta = before target, positive = after
            if (delta < -beforeSeconds || delta > afterSeconds)
            {
                continue;
            }

            var distance = Math.Abs(delta);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestMidpoint = midpoint;
            }
        }

        return (long)(bestMidpoint * TimeSpan.TicksPerSecond);
    }

    private async Task<IReadOnlyList<(double Start, double End)>> GetOrDetectSilenceAsync(
        string filePath,
        double scanStart,
        double scanDuration,
        int noisedB,
        double minDuration,
        CancellationToken cancellationToken)
    {
        var key = new SilenceCacheKey(
            filePath,
            (long)(scanStart * TimeSpan.TicksPerSecond),
            (long)(scanDuration * TimeSpan.TicksPerSecond),
            noisedB,
            (long)(minDuration * TimeSpan.TicksPerSecond));

        if (_silenceCache.TryGet(key, out var cached))
        {
            return cached;
        }

        var detected = await _blackFrameService.DetectSilenceAsync(
            filePath, scanStart, scanDuration, noisedB, minDuration, cancellationToken).ConfigureAwait(false);

        _silenceCache.AddOrUpdate(key, detected);
        return detected;
    }

    private readonly record struct SilenceCacheKey(
        string FilePath,
        long ScanStartTicks,
        long ScanDurationTicks,
        int NoiseDb,
        long MinDurationTicks);
}
