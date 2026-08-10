using System;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests;

public sealed class AnalysisGroupingTests
{
    [Fact]
    public void GetContainerId_EpisodeWithSeriesId_ReturnsSeriesId()
    {
        var seriesId = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };

        Assert.Equal(seriesId, AnalysisGrouping.GetContainerId(episode));
    }

    [Fact]
    public void GetContainerId_Movie_ReturnsOwnId()
    {
        var movie = new Movie { Id = Guid.NewGuid() };

        Assert.Equal(movie.Id, AnalysisGrouping.GetContainerId(movie));
    }

    [Fact]
    public void GetContainerId_AlternateVersion_ReturnsPrimaryVersionId()
    {
        var primaryId = Guid.NewGuid();
        var version = new Movie { Id = Guid.NewGuid(), PrimaryVersionId = primaryId };

        Assert.Equal(primaryId, AnalysisGrouping.GetContainerId(version));
    }

    [Fact]
    public void GetContainerId_EpisodeWithSeriesIdAndPrimaryVersion_SeriesWins()
    {
        var seriesId = Guid.NewGuid();
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            PrimaryVersionId = Guid.NewGuid(),
        };

        Assert.Equal(seriesId, AnalysisGrouping.GetContainerId(episode));
    }

    [Fact]
    public void GetContainerId_NullItem_ReturnsFallback()
    {
        var fallback = Guid.NewGuid();

        Assert.Equal(fallback, AnalysisGrouping.GetContainerId(null, fallback));
    }

    [Fact]
    public void GetContainerId_NullItemWithoutFallbackOverload_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AnalysisGrouping.GetContainerId(null!));
    }
}
