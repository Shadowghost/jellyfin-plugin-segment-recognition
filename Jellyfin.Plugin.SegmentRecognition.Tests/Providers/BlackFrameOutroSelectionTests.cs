using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Regression coverage for <see cref="BlackFrameProvider.FindBestOutroCluster"/>.
/// The bug it protects against: for item 7DFEE554-... (DEFCON-IV, 26:47 runtime) the
/// provider was returning a mid-episode cluster at 19:09 as the outro instead of the
/// real credits fade at 25:11, because the loop returned the FIRST matching cluster
/// rather than the latest one.
/// </summary>
public sealed class BlackFrameOutroSelectionTests
{
    private static long Sec(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    private static PluginConfiguration DefaultConfig() => new()
    {
        MinOutroDurationSeconds = 15,
        MaxOutroDurationSeconds = 600,
        MaxMovieOutroDurationSeconds = 900,
        MinIntroDurationSeconds = 15,
        MaxIntroDurationSeconds = 120,
    };

    [Fact]
    public void FindBestOutroCluster_PicksLatestMatchNotFirst()
    {
        // Reproduces the DEFCON-IV case: three clusters inside the [runtime-600s, runtime-15s]
        // outro window. The real credits fade is the latest one.
        var runtime = Sec(1606.647);
        var clusters = new List<(long Start, long End)>
        {
            (Sec(1149.295), Sec(1151.672)),
            (Sec(1511.657), Sec(1514.660)),
            (Sec(1605.500), Sec(1606.585)), // rejected: within 15s of end
        };

        var result = BlackFrameProvider.FindBestOutroCluster(Guid.NewGuid(), clusters, runtime, DefaultConfig());

        Assert.NotNull(result);
        Assert.Equal(MediaSegmentType.Outro, result!.Type);
        Assert.Equal(Sec(1511.657), result.StartTicks);
        Assert.Equal(runtime, result.EndTicks);
    }

    [Fact]
    public void FindBestOutroCluster_ReturnsNullWhenOnlyTooCloseToEnd()
    {
        var runtime = Sec(1600);
        var clusters = new List<(long Start, long End)>
        {
            (Sec(1595), Sec(1598)), // 5s from end, below 15s minimum
        };

        var result = BlackFrameProvider.FindBestOutroCluster(Guid.NewGuid(), clusters, runtime, DefaultConfig());

        Assert.Null(result);
    }

    [Fact]
    public void FindBestOutroCluster_ReturnsNullWhenNoClusters()
    {
        var result = BlackFrameProvider.FindBestOutroCluster(
            Guid.NewGuid(), new List<(long Start, long End)>(), Sec(1600), DefaultConfig());
        Assert.Null(result);
    }

    [Fact]
    public void FindBestOutroCluster_RejectsClustersBeyondMaxOutroWindow()
    {
        // Cluster 1000s from end, config allows only 600s max -> rejected.
        var runtime = Sec(2000);
        var clusters = new List<(long Start, long End)>
        {
            (Sec(1000), Sec(1005)), // 1000s from end > 600s max
        };

        var result = BlackFrameProvider.FindBestOutroCluster(Guid.NewGuid(), clusters, runtime, DefaultConfig());

        Assert.Null(result);
    }

    [Fact]
    public void FindBestOutroCluster_UsesMovieWindowWhenFlagged()
    {
        // 850s from end: fails episode (max 600) but fits movie (max 900).
        var runtime = Sec(5000);
        var clusters = new List<(long Start, long End)>
        {
            (Sec(4150), Sec(4155)),
        };

        var asEpisode = BlackFrameProvider.FindBestOutroCluster(Guid.NewGuid(), clusters, runtime, DefaultConfig(), isMovie: false);
        var asMovie = BlackFrameProvider.FindBestOutroCluster(Guid.NewGuid(), clusters, runtime, DefaultConfig(), isMovie: true);

        Assert.Null(asEpisode);
        Assert.NotNull(asMovie);
        Assert.Equal(Sec(4150), asMovie!.StartTicks);
    }

    /// <summary>
    /// Builds black-frame timestamps at a 24 fps sampling interval over <paramref name="spans"/>.
    /// </summary>
    private static List<long> BlackFramesAt(params (double Start, double End)[] spans)
    {
        const double Step = 1.0 / 24;
        var ticks = new List<long>();
        foreach (var (start, end) in spans)
        {
            for (var t = start; t <= end; t += Step)
            {
                ticks.Add(Sec(t));
            }
        }

        return ticks;
    }

    /// <summary>
    /// Roll credits interrupted by logo cards: the cluster pass keeps the last stretch and starts
    /// the outro 105 s inside the credits, the sustained region starts where they do.
    /// </summary>
    [Fact]
    public void FindBestOutroCluster_PrefersSustainedRegionOverLastFragment()
    {
        var runtime = Sec(3600);
        var black = BlackFramesAt((3420, 3470), (3475, 3520), (3525, 3600));
        var clusters = new List<(long Start, long End)>
        {
            (Sec(3420), Sec(3470)),
            (Sec(3475), Sec(3520)),
            (Sec(3525), Sec(3600)),
        };

        var result = BlackFrameProvider.FindBestOutroCluster(
            Guid.NewGuid(), clusters, runtime, DefaultConfig(), isMovie: false, blackTicks: black);

        Assert.NotNull(result);
        Assert.Equal(MediaSegmentType.Outro, result!.Type);
        Assert.Equal(Sec(3420), result.StartTicks);
        Assert.Equal(runtime, result.EndTicks);
    }

    /// <summary>
    /// An ending over artwork has no sustained black region, only the fade into it, so the
    /// cluster pass has to keep working there.
    /// </summary>
    [Fact]
    public void FindBestOutroCluster_FallsBackToClusterWhenNoSustainedRegion()
    {
        var runtime = Sec(1420);
        var black = BlackFramesAt((1300, 1301.5));
        var clusters = new List<(long Start, long End)> { (Sec(1300), Sec(1301.5)) };

        var result = BlackFrameProvider.FindBestOutroCluster(
            Guid.NewGuid(), clusters, runtime, DefaultConfig(), isMovie: false, blackTicks: black);

        Assert.NotNull(result);
        Assert.Equal(Sec(1300), result!.StartTicks);
    }

    /// <summary>
    /// Scattered fades are not credits - the span between them is mostly picture - so coverage
    /// keeps them out rather than reporting a four-minute outro.
    /// </summary>
    [Fact]
    public void FindDenseBlackRegionStart_IgnoresScatteredFades()
    {
        var runtime = Sec(1420);
        var black = BlackFramesAt((1200, 1201), (1260, 1261), (1320, 1321), (1380, 1381));

        Assert.Null(BlackFrameProvider.FindDenseBlackRegionStart(black, runtime, DefaultConfig()));
    }

    /// <summary>
    /// The cached rows carry intro frames too, which must not pull the outro region forward.
    /// </summary>
    [Fact]
    public void FindDenseBlackRegionStart_IgnoresFramesBeforeTheOutroRegion()
    {
        var runtime = Sec(1420);
        var config = DefaultConfig();
        config.OutroAnalysisSeconds = 240;
        var black = BlackFramesAt((5, 60), (1240, 1420));

        var start = BlackFrameProvider.FindDenseBlackRegionStart(black, runtime, config);

        Assert.Equal(Sec(1240), start);
    }

    /// <summary>
    /// Nothing to infer a sampling interval from means no region, not a guess.
    /// </summary>
    [Fact]
    public void FindDenseBlackRegionStart_ReturnsNullWithoutEnoughFrames()
    {
        Assert.Null(BlackFrameProvider.FindDenseBlackRegionStart(null, Sec(1420), DefaultConfig()));
        Assert.Null(BlackFrameProvider.FindDenseBlackRegionStart([Sec(1400)], Sec(1420), DefaultConfig()));
    }
}
