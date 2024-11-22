namespace Jellyfin.Plugin.SegmentRecognition.Tests;

using System;
using System.Collections.ObjectModel;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Chapter analyzer tests.
/// </summary>
public class TestChapterAnalyzer
{
    /// <summary>
    /// Tests introduction recognition.
    /// </summary>
    [Theory]
    [InlineData("Opening")]
    [InlineData("OP")]
    [InlineData("Intro")]
    [InlineData("Intro Start")]
    [InlineData("Introduction")]
    public void TestIntroductionExpression(string chapterName)
    {
        var chapters = CreateChapters(chapterName, MediaSegmentType.Intro);
        var introChapter = FindChapter(chapters, MediaSegmentType.Intro);

        Assert.NotNull(introChapter);
        Assert.Equal(60, introChapter.IntroStart);
        Assert.Equal(90, introChapter.IntroEnd);
    }

    /// <summary>
    /// Tests end credits recognition.
    /// </summary>
    [Theory]
    [InlineData("End Credits")]
    [InlineData("Ending")]
    [InlineData("Credit start")]
    [InlineData("Closing Credits")]
    [InlineData("Credits")]
    public void TestEndCreditsExpression(string chapterName)
    {
        var chapters = CreateChapters(chapterName, MediaSegmentType.Outro);
        var creditsChapter = FindChapter(chapters, MediaSegmentType.Outro);

        Assert.NotNull(creditsChapter);
        Assert.Equal(1890, creditsChapter.IntroStart);
        Assert.Equal(2000, creditsChapter.IntroEnd);
    }

    private Intro? FindChapter(Collection<ChapterInfo> chapters, MediaSegmentType mode)
    {
        var logger = new LoggerFactory().CreateLogger<ChapterAnalyzer>();
        var analyzer = new ChapterAnalyzer(logger);

        var config = new Configuration.PluginConfiguration();
        var expression = mode == MediaSegmentType.Intro ?
            config.ChapterAnalyzerIntroductionPattern :
            config.ChapterAnalyzerEndCreditsPattern;

        return analyzer.FindMatchingChapter(new() { Duration = 2000 }, chapters, expression, mode);
    }

    private Collection<ChapterInfo> CreateChapters(string name, MediaSegmentType mode)
    {
        var chapters = new[]{
            CreateChapter("Cold Open", 0),
            CreateChapter(mode == MediaSegmentType.Intro ? name : "Introduction", 60),
            CreateChapter("Main Episode", 90),
            CreateChapter(mode == MediaSegmentType.Outro ? name : "Credits", 1890)
        };

        return [.. chapters];
    }

    /// <summary>
    /// Create a ChapterInfo object.
    /// </summary>
    /// <param name="name">Chapter name.</param>
    /// <param name="position">Chapter position (in seconds).</param>
    /// <returns>ChapterInfo.</returns>
    private ChapterInfo CreateChapter(string name, int position)
    {
        return new()
        {
            Name = name,
            StartPositionTicks = TimeSpan.FromSeconds(position).Ticks
        };
    }
}
