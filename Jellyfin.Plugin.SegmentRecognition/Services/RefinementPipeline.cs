using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Configuration;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Centralized refinement pipeline that applies silence, chapter, and keyframe snapping
/// to raw segment boundaries. Used by both BlackFrameProvider and
/// ChromaprintProvider to avoid duplicating the pipeline logic.
/// </summary>
public class RefinementPipeline
{
    private readonly SegmentRefiner _segmentRefiner;
    private readonly ChapterSnapper _chapterSnapper;
    private readonly KeyframeSnapper _keyframeSnapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefinementPipeline"/> class.
    /// </summary>
    /// <param name="segmentRefiner">The segment refiner.</param>
    /// <param name="chapterSnapper">The chapter snapper.</param>
    /// <param name="keyframeSnapper">The keyframe snapper.</param>
    public RefinementPipeline(
        SegmentRefiner segmentRefiner,
        ChapterSnapper chapterSnapper,
        KeyframeSnapper keyframeSnapper)
    {
        _segmentRefiner = segmentRefiner;
        _chapterSnapper = chapterSnapper;
        _keyframeSnapper = keyframeSnapper;
    }

    /// <summary>
    /// Applies the full refinement pipeline to raw segment boundaries:
    /// 1. Silence-based boundary snapping
    /// 2. Chapter marker snapping
    /// 3. Keyframe snapping.
    /// </summary>
    /// <param name="itemId">The item identifier (for chapter and keyframe lookups).</param>
    /// <param name="startTicks">The raw start position in ticks.</param>
    /// <param name="endTicks">The raw end position in ticks.</param>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="videoCodec">The video codec name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of refined (StartTicks, EndTicks).</returns>
    public async Task<(long StartTicks, long EndTicks)> RefineAsync(
        Guid itemId,
        long startTicks,
        long endTicks,
        string filePath,
        string? videoCodec,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Step 1: Silence refinement
        var (refinedStart, refinedEnd) = await _segmentRefiner.RefineSegmentAsync(
            startTicks, endTicks, filePath, videoCodec, cancellationToken).ConfigureAwait(false);

        // Step 2: Chapter snapping
        refinedStart = _chapterSnapper.SnapToChapter(itemId, refinedStart, cancellationToken);
        refinedEnd = _chapterSnapper.SnapToChapter(itemId, refinedEnd, cancellationToken);

        // Step 3: Keyframe snapping
        var windowTicks = (long)(config.KeyframeSnapWindowSeconds * TimeSpan.TicksPerSecond);
        refinedStart = _keyframeSnapper.SnapToKeyframe(
            itemId, refinedStart, windowTicks, snapBefore: true, cancellationToken);
        refinedEnd = _keyframeSnapper.SnapToKeyframe(
            itemId, refinedEnd, windowTicks, snapBefore: false, cancellationToken);

        return (refinedStart, refinedEnd);
    }
}
