using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Covers the adaptive black-frame threshold.
/// </summary>
/// <remarks>
/// The configured percentage is rescaled into the range the scanned content leaves available, so
/// material that never reaches full brightness does not have its ordinary dark scenes counted as
/// transitions. Material with real white frames keeps the percentage it was given.
/// </remarks>
public sealed class BlackFrameThresholdTests
{
    private const long Second = 10_000_000;

    [Fact]
    public void BrightContentKeepsTheConfiguredPercentage()
    {
        var scan = Enumerable.Range(0, 100).Select(i => i < 99 ? 0.0 : 100.0).ToList();

        Assert.Equal(85, BlackFrameThresholdHelper.NormalizeThreshold(scan, 85), 3);
    }

    [Fact]
    public void DarkContentRaisesTheBar()
    {
        var scan = Enumerable.Repeat(40.0, 100).ToList();

        // Floor is capped at 30, so: 85 * (100 - 30) / 100 + 30.
        Assert.Equal(89.5, BlackFrameThresholdHelper.NormalizeThreshold(scan, 85), 3);
    }

    /// <summary>
    /// A scan with no bright frames at all still yields a usable bar.
    /// </summary>
    /// <remarks>
    /// The cap is what keeps a scan that is black nearly end to end - a long fade, a region that
    /// caught only credits - from raising the bar until nothing qualifies at all.
    /// </remarks>
    [Fact]
    public void AllBlackScanIsBoundedByTheFloorCap()
    {
        var scan = Enumerable.Repeat(100.0, 500).ToList();

        Assert.Equal(89.5, BlackFrameThresholdHelper.NormalizeThreshold(scan, 85), 3);
    }

    /// <summary>
    /// The baseline survives one bright frame in an otherwise dark scan.
    /// </summary>
    /// <remarks>
    /// A single stray bright frame must not drag the floor to zero, which is why the baseline is a
    /// percentile rather than the minimum.
    /// </remarks>
    [Fact]
    public void OneBrightFrameDoesNotResetTheFloor()
    {
        var scan = Enumerable.Repeat(50.0, 200).ToList();
        scan[0] = 0.0;

        Assert.Equal(89.5, BlackFrameThresholdHelper.NormalizeThreshold(scan, 85), 3);
    }

    [Fact]
    public void EmptyScanFallsBackToTheConfiguredPercentage()
    {
        Assert.Equal(85, BlackFrameThresholdHelper.NormalizeThreshold([], 85), 3);
    }

    /// <summary>
    /// Selection keeps the real transition and drops the dark scene around it.
    /// </summary>
    /// <remarks>
    /// The point of the exercise: on dark material, frames a fixed bar would have accepted as a
    /// transition are dropped, while the genuinely black run survives.
    /// </remarks>
    [Fact]
    public void SelectionDropsDarkSceneFramesButKeepsTheBlackRun()
    {
        var scan = new List<(long TimestampTicks, double BlackPercentage)>();
        for (var i = 0; i < 100; i++)
        {
            // A dark scene that sits just above a fixed 85% bar.
            scan.Add((i * Second, 87.0));
        }

        for (var i = 100; i < 110; i++)
        {
            scan.Add((i * Second, 99.0));
        }

        var selected = BlackFrameProvider.SelectBlackFrames(scan, 85);

        Assert.Equal(10, selected.Count);
        Assert.All(selected, f => Assert.Equal(99.0, f.BlackPercentage));
    }

    [Fact]
    public void SelectionOnBrightContentIsTheConfiguredPercentage()
    {
        var scan = new List<(long TimestampTicks, double BlackPercentage)>();
        for (var i = 0; i < 100; i++)
        {
            scan.Add((i * Second, 0.0));
        }

        scan.Add((100 * Second, 86.0));
        scan.Add((101 * Second, 100.0));

        var selected = BlackFrameProvider.SelectBlackFrames(scan, 85);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SelectionOfEmptyScanIsEmpty()
    {
        Assert.Empty(BlackFrameProvider.SelectBlackFrames([], 85));
    }
}
