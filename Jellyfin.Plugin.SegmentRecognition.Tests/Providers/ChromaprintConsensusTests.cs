using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests for the consensus rule that picks a chromaprint match from per-counterpart candidates.
/// </summary>
/// <remarks>
/// The provider used to accept the first counterpart that happened to produce an in-window region
/// and stop. That made the outcome depend on database row order, and let a single spurious pairing
/// define an item's intro for good.
/// </remarks>
public sealed class ChromaprintConsensusTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    private static (long StartTicks, long EndTicks) Region(double startSeconds, double endSeconds)
        => ((long)(startSeconds * Second), (long)(endSeconds * Second));

    [Fact]
    public void NoCandidates_ReturnsNull()
    {
        Assert.Null(ChromaprintProvider.SelectConsensusRegion([], counterpartsCompared: 4));
    }

    /// <summary>
    /// The core fix: one counterpart out of several disagreeing with the rest is not enough.
    /// </summary>
    [Fact]
    public void SingleOutlierAmongManyCounterparts_IsRejected()
    {
        var candidates = new List<(long, long)> { Region(300, 390) };

        Assert.Null(ChromaprintProvider.SelectConsensusRegion(candidates, counterpartsCompared: 5));
    }

    [Fact]
    public void TwoAgreeingCounterparts_Win()
    {
        var candidates = new List<(long, long)>
        {
            Region(10, 100),
            Region(10.5, 100.5),
        };

        var result = ChromaprintProvider.SelectConsensusRegion(candidates, counterpartsCompared: 4);

        Assert.NotNull(result);
        Assert.InRange(result!.Value.StartTicks, Region(10, 0).Item1, Region(10.5, 0).Item1);
    }

    /// <summary>
    /// The majority cluster wins even when an outlier sorts first.
    /// </summary>
    [Fact]
    public void MajorityClusterBeatsEarlierOutlier()
    {
        var candidates = new List<(long, long)>
        {
            Region(5, 95),      // lone outlier, sorts first
            Region(30, 120),
            Region(30.4, 120.4),
            Region(30.8, 120.8),
        };

        var result = ChromaprintProvider.SelectConsensusRegion(candidates, counterpartsCompared: 4);

        Assert.NotNull(result);
        Assert.Equal(Region(30.4, 0).Item1, result!.Value.StartTicks);
    }

    /// <summary>
    /// With only one comparable counterpart (a two-episode season) a single vote is all that can
    /// exist, so it is accepted.
    /// </summary>
    [Fact]
    public void SingleCounterpart_AcceptsSingleVote()
    {
        var candidates = new List<(long, long)> { Region(10, 100) };

        var result = ChromaprintProvider.SelectConsensusRegion(candidates, counterpartsCompared: 1);

        Assert.NotNull(result);
        Assert.Equal(Region(10, 0).Item1, result!.Value.StartTicks);
    }

    /// <summary>
    /// The representative is the cluster median, so one member with a stretched end cannot drag
    /// the boundary out.
    /// </summary>
    [Fact]
    public void UsesMedianSoOneStretchedMemberCannotSkewTheResult()
    {
        var candidates = new List<(long, long)>
        {
            Region(30, 120),
            Region(30.2, 121),
            Region(30.4, 400),
        };

        var result = ChromaprintProvider.SelectConsensusRegion(candidates, counterpartsCompared: 3);

        Assert.NotNull(result);
        Assert.Equal(Region(0, 121).Item2, result!.Value.EndTicks);
    }

    /// <summary>
    /// Ordering of the input must not change the outcome; the old first-wins behaviour depended
    /// entirely on it.
    /// </summary>
    [Fact]
    public void ResultIsIndependentOfInputOrder()
    {
        var forward = new List<(long, long)>
        {
            Region(30, 120),
            Region(30.4, 120.4),
            Region(5, 95),
        };
        var reversed = new List<(long, long)>
        {
            Region(5, 95),
            Region(30.4, 120.4),
            Region(30, 120),
        };

        Assert.Equal(
            ChromaprintProvider.SelectConsensusRegion(forward, 3),
            ChromaprintProvider.SelectConsensusRegion(reversed, 3));
    }

    /// <summary>
    /// Two equally-supported clusters resolve to the earlier one, deterministically.
    /// </summary>
    [Fact]
    public void TiedClusters_ResolveToTheEarlierOne()
    {
        var candidates = new List<(long, long)>
        {
            Region(10, 100),
            Region(10.2, 100.2),
            Region(300, 390),
            Region(300.2, 390.2),
        };

        var result = ChromaprintProvider.SelectConsensusRegion(candidates, counterpartsCompared: 4);

        Assert.NotNull(result);
        Assert.Equal(Region(10.2, 0).Item1, result!.Value.StartTicks);
    }
}
