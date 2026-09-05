using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Normalizes the black-frame percentage threshold against the darkness a scan actually contains.
/// </summary>
/// <remarks>
/// A fixed percentage assumes every source reaches the same baseline brightness. Dark-graded
/// material does not: when even the least-black frame of a scan is a third black pixels, a fixed
/// bar lets ordinary night scenes qualify as a transition. Rescaling the configured percentage
/// into the range the content leaves available raises the bar exactly as far as the content is
/// dark, and leaves it untouched on material that has real white frames.
/// </remarks>
internal static class BlackFrameThresholdHelper
{
    /// <summary>
    /// Ceiling on the measured darkness floor.
    /// </summary>
    /// <remarks>
    /// Without it a scan that is black nearly end to end - a long fade, a region that caught only
    /// credits - would push the bar up to where nothing qualifies at all.
    /// </remarks>
    internal const double MaxFloor = 30;

    /// <summary>Percentile of the darkness distribution taken as the content's baseline.</summary>
    internal const double FloorPercentile = 0.01;

    /// <summary>
    /// Normalizes the configured minimum percentage against the darkest frames in a scan.
    /// </summary>
    /// <param name="frames">The unfiltered scan results. Must not be empty.</param>
    /// <param name="minimumPercentage">The configured minimum black percentage.</param>
    /// <returns>The normalized minimum percentage a frame has to reach to count as black.</returns>
    internal static double NormalizeThreshold(
        IReadOnlyList<double> frames,
        double minimumPercentage)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            return minimumPercentage;
        }

        var ordered = frames.Order().ToList();

        // Clamp into range: on a short scan frames.Count * 0.01 floors to 0, so the floor becomes
        // the single least-black frame. The MaxFloor cap bounds that frame's influence.
        var percentileIndex = Math.Clamp((int)(frames.Count * FloorPercentile), 0, frames.Count - 1);
        var floor = Math.Min(ordered[percentileIndex], MaxFloor);

        return (minimumPercentage * (100 - floor) / 100) + floor;
    }
}
