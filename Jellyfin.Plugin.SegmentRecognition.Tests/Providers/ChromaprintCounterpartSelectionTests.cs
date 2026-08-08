using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests for which counterparts an item is compared against.
/// </summary>
/// <remarks>
/// The provider used to take the first N rows of the group. In a season whose opening changes
/// partway through - a split-cour anime carrying two different OPs under one Jellyfin season -
/// that meant every episode past the Nth was only ever compared against the head of the season,
/// and could therefore only match audio the two halves have in common: the distributor ident at
/// the front of every file.
/// </remarks>
public sealed class ChromaprintCounterpartSelectionTests
{
    [Fact]
    public void NeighboursByDistance_ExcludesSelfAndCoversEveryOtherIndex()
    {
        var neighbours = ChromaprintProvider.NeighboursByDistance(3, 10).ToList();

        Assert.DoesNotContain(3, neighbours);
        Assert.Equal(9, neighbours.Count);
        Assert.Equal(Enumerable.Range(0, 10).Where(i => i != 3), neighbours.OrderBy(i => i));
    }

    [Fact]
    public void NeighboursByDistance_WalksOutwardsAlternatingDirection()
    {
        Assert.Equal([4, 6, 3, 7, 2, 8, 1, 9, 0], ChromaprintProvider.NeighboursByDistance(5, 10));
    }

    [Fact]
    public void NeighboursByDistance_AtAnEdge_ContinuesInTheOnlyAvailableDirection()
    {
        Assert.Equal([1, 2, 3, 4], ChromaprintProvider.NeighboursByDistance(0, 5));
        Assert.Equal([3, 2, 1, 0], ChromaprintProvider.NeighboursByDistance(4, 5));
    }

    [Fact]
    public void NeighboursByDistance_SingleItemGroup_YieldsNothing()
    {
        Assert.Empty(ChromaprintProvider.NeighboursByDistance(0, 1));
    }

    /// <summary>
    /// The regression this selection exists for: a 24-episode season that switches opening at
    /// episode 13. Every episode must get at least the two same-opening counterparts the consensus
    /// rule requires, which the old "first 8 rows" selection gave to none of episodes 13-24.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(23)]
    public void SplitCourSeason_EveryEpisodeSeesTwoCounterpartsFromItsOwnCour(int index)
    {
        const int SeasonLength = 24;
        const int CourBoundary = 12;
        const int MaxCounterparts = 8;

        var compared = ChromaprintProvider.NeighboursByDistance(index, SeasonLength)
            .Take(MaxCounterparts)
            .ToList();

        var sameCour = compared.Count(j => (j < CourBoundary) == (index < CourBoundary));

        Assert.True(sameCour >= 2, $"episode index {index} only saw {sameCour} same-cour counterpart(s)");
    }
}
