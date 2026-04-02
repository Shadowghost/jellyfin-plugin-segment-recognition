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
}
