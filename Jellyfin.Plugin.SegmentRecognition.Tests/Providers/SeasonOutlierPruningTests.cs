using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests for discarding segments that sit where the rest of the season has none.
/// </summary>
/// <remarks>
/// An episode with no intro of its own can still produce one by matching an incidental music cue
/// shared with a sibling; two agreeing counterparts is all consensus requires. The shapes below
/// are taken from real seasons in a library of ~49 000 intros and ~40 000 outros.
/// </remarks>
public sealed class SeasonOutlierPruningTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    /// <summary>Tolerance for intros, measured from the start of the file.</summary>
    private const long IntroTolerance = 120 * Second;

    /// <summary>Tolerance for outros, measured back from the end of the file.</summary>
    private const long OutroTolerance = 30 * Second;

    /// <summary>
    /// Builds a season from (count, positionSeconds) groups, jittering members apart so the test
    /// never depends on identical tick values.
    /// </summary>
    private static List<(Guid ItemId, long PositionTicks)> Season(params (int Count, double PositionSeconds)[] groups)
    {
        var items = new List<(Guid, long)>();
        foreach (var (count, position) in groups)
        {
            for (int i = 0; i < count; i++)
            {
                items.Add((Guid.NewGuid(), (long)((position + (i * 0.4)) * Second)));
            }
        }

        return items;
    }

    // ---------------------------------------------------------------- intros

    /// <summary>
    /// The Alias season this was built for: seventeen episodes agree on 0 s, and three episodes
    /// that have no title sequence at all produced matches at 218 s and ~497 s.
    /// </summary>
    [Fact]
    public void ScatteredStragglersAgainstADominantPosition_AreDropped()
    {
        var season = Season((17, 0), (1, 217.8), (2, 490.7));

        var dropped = ChromaprintProvider.SelectSeasonOutliers(season, IntroTolerance).ToHashSet();

        Assert.Equal(3, dropped.Count);
        Assert.All(
            season.Where(s => s.PositionTicks < 120 * Second),
            s => Assert.DoesNotContain(s.ItemId, dropped));
    }

    /// <summary>
    /// The case a distance-from-average rule got wrong: a 197-episode arc carries three genuine
    /// intro positions because the cold open before the titles varies in length. Every one of them
    /// is corroborated by enough episodes to be a format variant, so nothing may be dropped.
    /// </summary>
    [Fact]
    public void SeveralWellSupportedPositions_AreAllKept()
    {
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(
            Season((141, 0), (41, 196), (15, 272)), IntroTolerance));
    }

    /// <summary>
    /// With no dominant position the season's matching is simply unreliable; guessing which of the
    /// scattered results are wrong would discard as many good ones as bad.
    /// </summary>
    [Fact]
    public void NoDominantPosition_LeavesTheSeasonAlone()
    {
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(
            Season((2, 0), (2, 271), (2, 435)), IntroTolerance));
    }

    /// <summary>
    /// Three episodes agreeing is a variant, not noise - the bar sits above the two counterparts
    /// that consensus itself requires.
    /// </summary>
    [Fact]
    public void ClusterOfThree_SurvivesButAPairDoesNot()
    {
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(
            Season((20, 0), (3, 300)), IntroTolerance));

        Assert.Equal(
            2,
            ChromaprintProvider.SelectSeasonOutliers(Season((20, 0), (2, 300)), IntroTolerance).Count);
    }

    /// <summary>
    /// Positions drift by tens of seconds within a season as cold-open lengths vary; that must not
    /// split one position into several undersized clusters.
    /// </summary>
    [Fact]
    public void DriftWithinAPositionDoesNotSplitIt()
    {
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(
            Season((1, 60), (1, 75), (1, 92), (1, 110), (1, 128), (1, 150), (1, 168)), IntroTolerance));
    }

    /// <summary>
    /// A real season whose cold open varies continuously: intros run from 22s to 292s with no
    /// consecutive gap above 93s, all correct.
    /// </summary>
    /// <remarks>
    /// Chaining each candidate against the cluster's <em>first</em> member split this run into
    /// 6/3/1 and pruned the 292s episode, whose intro is genuinely there and only 93s from its
    /// nearest neighbour - well inside the tolerance it was supposedly judged by. Chaining against
    /// the previous member keeps the run whole.
    /// </remarks>
    [Fact]
    public void ContinuousSpreadOfPositions_IsNotFragmented()
    {
        var season = Season(
            (1, 22), (1, 45), (1, 57), (1, 79), (1, 95),
            (1, 139), (1, 152), (1, 161), (1, 199), (1, 292));

        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(season, IntroTolerance));
    }

    /// <summary>
    /// The looser chaining must not shelter a genuine outlier: one that sits beyond the tolerance
    /// from every member, not merely from the first, is still dropped.
    /// </summary>
    [Fact]
    public void GapWiderThanToleranceStillSeparates()
    {
        var season = Season((1, 22), (1, 45), (1, 57), (1, 79), (1, 95), (1, 139), (1, 400));

        Assert.Single(ChromaprintProvider.SelectSeasonOutliers(season, IntroTolerance));
    }

    /// <summary>
    /// A short season carries too little evidence: three of five agreeing is a "majority" that
    /// means nothing.
    /// </summary>
    [Fact]
    public void SeasonTooSmallToJudge_IsLeftAlone()
    {
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(Season((4, 0), (1, 400)), IntroTolerance));
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers([], IntroTolerance));
    }

    /// <summary>
    /// A season where every episode agrees has nothing to prune.
    /// </summary>
    [Fact]
    public void FullyConsistentSeason_IsUntouched()
    {
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(Season((12, 45)), IntroTolerance));
    }

    /// <summary>
    /// The result must not depend on the order rows came back in.
    /// </summary>
    [Fact]
    public void ResultIsIndependentOfInputOrder()
    {
        var season = Season((10, 0), (1, 300), (2, 520));

        Assert.Equal(
            ChromaprintProvider.SelectSeasonOutliers(season, IntroTolerance).ToHashSet(),
            ChromaprintProvider.SelectSeasonOutliers(Enumerable.Reverse(season).ToList(), IntroTolerance).ToHashSet());
    }

    // ---------------------------------------------------------------- outros

    /// <summary>
    /// Outros are fed in end-relative, where a season is far tighter: the credits length is fixed
    /// even when runtimes are not. Stragglers inside the credits window still stand out.
    /// </summary>
    [Fact]
    public void OutroStragglers_AreDropped()
    {
        var season = Season((18, 18), (2, 98), (1, 178));

        var dropped = ChromaprintProvider.SelectSeasonOutliers(season, OutroTolerance).ToHashSet();

        Assert.Equal(3, dropped.Count);
        Assert.All(
            season.Where(s => s.PositionTicks < 50 * Second),
            s => Assert.DoesNotContain(s.ItemId, dropped));
    }

    /// <summary>
    /// A season that runs its credits at two lengths - episodes with a next-episode preview after
    /// the credits sit further from the end than those without - keeps both, because each is
    /// corroborated by enough episodes.
    /// </summary>
    [Fact]
    public void TwoLegitimateCreditsPositions_AreBothKept()
    {
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(
            Season((13, 21), (29, 111)), OutroTolerance));
    }

    /// <summary>
    /// The outro tolerance has to be much tighter than the intro's. The whole outro population
    /// lives inside the last few minutes of the file, so at the intro's 120 s every straggler
    /// chains into the dominant cluster and none is seen at all.
    /// </summary>
    [Fact]
    public void IntroToleranceWouldBeTooCoarseForOutros()
    {
        var season = Season((18, 18), (2, 98), (1, 178));

        Assert.Equal(3, ChromaprintProvider.SelectSeasonOutliers(season, OutroTolerance).Count);
        Assert.Empty(ChromaprintProvider.SelectSeasonOutliers(season, IntroTolerance));
    }
}
