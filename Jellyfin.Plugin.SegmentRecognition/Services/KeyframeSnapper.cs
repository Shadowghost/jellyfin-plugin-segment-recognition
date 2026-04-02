using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using MediaBrowser.Controller.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Snaps segment boundaries to nearby video keyframes using Jellyfin's keyframe data.
/// </summary>
public class KeyframeSnapper
{
    private readonly IKeyframeManager _keyframeManager;
    private readonly ILogger<KeyframeSnapper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyframeSnapper"/> class.
    /// </summary>
    /// <param name="keyframeManager">The keyframe manager.</param>
    /// <param name="logger">The logger.</param>
    public KeyframeSnapper(
        IKeyframeManager keyframeManager,
        ILogger<KeyframeSnapper> logger)
    {
        _keyframeManager = keyframeManager;
        _logger = logger;
    }

    /// <summary>
    /// Snaps a target tick value to the nearest keyframe within the configured window.
    /// For segment starts, snaps to the keyframe AT OR BEFORE the target.
    /// For segment ends, snaps to the keyframe AT OR AFTER the target.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="targetTicks">The target position in ticks.</param>
    /// <param name="maxWindowTicks">Maximum distance from target to search for keyframes.</param>
    /// <param name="snapBefore">If true, snap to keyframe at or before target; if false, at or after.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapped tick value, or the original target if no keyframe is found within the window.</returns>
    public long SnapToKeyframe(
        Guid itemId,
        long targetTicks,
        long maxWindowTicks,
        bool snapBefore,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableKeyframeSnapping)
        {
            return targetTicks;
        }

        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<long> keyframeTicks;
        try
        {
            var keyframeDataList = _keyframeManager.GetKeyframeData(itemId);
            if (keyframeDataList.Count == 0)
            {
                _logger.LogDebug("No keyframe data available for item {ItemId}, skipping keyframe snap", itemId);
                return targetTicks;
            }

            keyframeTicks = keyframeDataList[0].KeyframeTicks;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get keyframe data for item {ItemId}, skipping keyframe snap", itemId);
            return targetTicks;
        }

        if (keyframeTicks.Count == 0)
        {
            return targetTicks;
        }

        // Binary search for the nearest keyframe
        var index = BinarySearchNearest(keyframeTicks, targetTicks);

        if (snapBefore)
        {
            // Find keyframe AT OR BEFORE target
            var candidate = index;
            while (candidate >= 0 && keyframeTicks[candidate] > targetTicks)
            {
                candidate--;
            }

            if (candidate >= 0 && Math.Abs(keyframeTicks[candidate] - targetTicks) <= maxWindowTicks)
            {
                _logger.LogDebug(
                    "Keyframe snap (before): {Original} -> {Snapped} for item {ItemId}",
                    targetTicks,
                    keyframeTicks[candidate],
                    itemId);
                return keyframeTicks[candidate];
            }
        }
        else
        {
            // Find keyframe AT OR AFTER target
            var candidate = index;
            while (candidate < keyframeTicks.Count && keyframeTicks[candidate] < targetTicks)
            {
                candidate++;
            }

            if (candidate < keyframeTicks.Count && Math.Abs(keyframeTicks[candidate] - targetTicks) <= maxWindowTicks)
            {
                _logger.LogDebug(
                    "Keyframe snap (after): {Original} -> {Snapped} for item {ItemId}",
                    targetTicks,
                    keyframeTicks[candidate],
                    itemId);
                return keyframeTicks[candidate];
            }
        }

        return targetTicks;
    }

    /// <summary>
    /// Performs a binary search to find the index of the keyframe nearest to the target.
    /// </summary>
    private static int BinarySearchNearest(IReadOnlyList<long> sortedTicks, long target)
    {
        int lo = 0;
        int hi = sortedTicks.Count - 1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (sortedTicks[mid] == target)
            {
                return mid;
            }

            if (sortedTicks[mid] < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // lo is the insertion point; return the closer of lo and lo-1
        if (lo >= sortedTicks.Count)
        {
            return sortedTicks.Count - 1;
        }

        if (lo == 0)
        {
            return 0;
        }

        return Math.Abs(sortedTicks[lo] - target) < Math.Abs(sortedTicks[lo - 1] - target) ? lo : lo - 1;
    }
}
