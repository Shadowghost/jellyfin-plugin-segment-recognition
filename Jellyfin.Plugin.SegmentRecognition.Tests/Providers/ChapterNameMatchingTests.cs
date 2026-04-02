using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using MediaBrowser.Model.MediaSegments;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

public class ChapterNameMatchingTests
{
    // --- BuildRegexes ---

    [Fact]
    public void BuildRegexes_DefaultConfig_CreatesAllFourTypes()
    {
        var config = new PluginConfiguration();

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.Contains(MediaSegmentType.Intro, regexes.Keys);
        Assert.Contains(MediaSegmentType.Outro, regexes.Keys);
        Assert.Contains(MediaSegmentType.Recap, regexes.Keys);
        Assert.Contains(MediaSegmentType.Preview, regexes.Keys);
    }

    [Fact]
    public void BuildRegexes_EmptyPatterns_OmitsType()
    {
        var config = new PluginConfiguration
        {
            IntroChapterNames = [],
            OutroChapterNames = ["Credits"],
            RecapChapterNames = [],
            PreviewChapterNames = []
        };

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.Single(regexes);
        Assert.Contains(MediaSegmentType.Outro, regexes.Keys);
    }

    [Fact]
    public void BuildRegexes_AllEmpty_ReturnsEmptyDictionary()
    {
        var config = new PluginConfiguration
        {
            IntroChapterNames = [],
            OutroChapterNames = [],
            RecapChapterNames = [],
            PreviewChapterNames = []
        };

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.Empty(regexes);
    }

    // --- Pattern matching behavior ---

    [Theory]
    [InlineData("Intro")]
    [InlineData("intro")]
    [InlineData("INTRO")]
    [InlineData("Opening")]
    [InlineData("Opening Credits")]
    [InlineData("OP")]
    public void BuildRegexes_DefaultConfig_MatchesIntroPatterns(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.Matches(regexes[MediaSegmentType.Intro], chapterName);
    }

    [Theory]
    [InlineData("Outro")]
    [InlineData("Credits")]
    [InlineData("End Credits")]
    [InlineData("Ending")]
    public void BuildRegexes_DefaultConfig_MatchesOutroPatterns(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.Matches(regexes[MediaSegmentType.Outro], chapterName);
    }

    [Theory]
    [InlineData("Recap")]
    [InlineData("Previously on")]
    public void BuildRegexes_DefaultConfig_MatchesRecapPatterns(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.Matches(regexes[MediaSegmentType.Recap], chapterName);
    }

    [Theory]
    [InlineData("Preview")]
    [InlineData("Next time")]
    [InlineData("Next Episode")]
    public void BuildRegexes_DefaultConfig_MatchesPreviewPatterns(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.Matches(regexes[MediaSegmentType.Preview], chapterName);
    }

    [Theory]
    [InlineData("Introvert")]
    [InlineData("Introspection")]
    public void BuildRegexes_DoesNotMatchSubstrings(string chapterName)
    {
        // "Intro" should not match "Introvert" because the boundary requires whitespace/colon/end
        var config = new PluginConfiguration
        {
            IntroChapterNames = ["Intro"],
            OutroChapterNames = [],
            RecapChapterNames = [],
            PreviewChapterNames = []
        };

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.DoesNotMatch(regexes[MediaSegmentType.Intro], chapterName);
    }

    [Fact]
    public void BuildRegexes_MatchesPatternAfterSpace()
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.Matches(regexes[MediaSegmentType.Intro], "Chapter 1: Intro");
    }

    [Fact]
    public void BuildRegexes_MatchesPatternWithColon()
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.Matches(regexes[MediaSegmentType.Intro], "Intro:");
    }

    [Fact]
    public void BuildRegexes_EscapesSpecialRegexChars()
    {
        var config = new PluginConfiguration
        {
            IntroChapterNames = ["Opening (credits)"],
            OutroChapterNames = [],
            RecapChapterNames = [],
            PreviewChapterNames = []
        };

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.Matches(regexes[MediaSegmentType.Intro], "Opening (credits)");
        // Without escaping, "(credits)" would be a regex group, not a literal match
        Assert.DoesNotMatch(regexes[MediaSegmentType.Intro], "Opening credits");
    }

    [Fact]
    public void BuildRegexes_NegativeLookahead_RejectsPatternFollowedByEnd()
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.DoesNotMatch(regexes[MediaSegmentType.Intro], "Intro End");
    }

    // --- IsValidDuration ---

    [Fact]
    public void IsValidDuration_ZeroDuration_ReturnsFalse()
    {
        var config = new PluginConfiguration();

        Assert.False(ChapterNameProvider.IsValidDuration(MediaSegmentType.Intro, 0, config, false));
    }

    [Fact]
    public void IsValidDuration_NegativeDuration_ReturnsFalse()
    {
        var config = new PluginConfiguration();

        Assert.False(ChapterNameProvider.IsValidDuration(MediaSegmentType.Intro, -5, config, false));
    }

    [Theory]
    [InlineData(15, true)]   // exact min
    [InlineData(60, true)]   // mid-range
    [InlineData(120, true)]  // exact max
    [InlineData(14, false)]  // below min
    [InlineData(121, false)] // above max
    public void IsValidDuration_Intro_RespectsDefaultBounds(double duration, bool expected)
    {
        var config = new PluginConfiguration();

        Assert.Equal(expected, ChapterNameProvider.IsValidDuration(MediaSegmentType.Intro, duration, config, false));
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(300, true)]
    [InlineData(600, true)]   // exact max
    [InlineData(14, false)]
    [InlineData(601, false)]
    public void IsValidDuration_Outro_RespectsDefaultBounds(double duration, bool expected)
    {
        var config = new PluginConfiguration();

        Assert.Equal(expected, ChapterNameProvider.IsValidDuration(MediaSegmentType.Outro, duration, config, false));
    }

    [Fact]
    public void IsValidDuration_MovieOutro_UsesMovieMaximum()
    {
        var config = new PluginConfiguration
        {
            MaxOutroDurationSeconds = 600,
            MaxMovieOutroDurationSeconds = 900
        };

        // 700s exceeds TV max (600) but not movie max (900)
        Assert.False(ChapterNameProvider.IsValidDuration(MediaSegmentType.Outro, 700, config, isMovie: false));
        Assert.True(ChapterNameProvider.IsValidDuration(MediaSegmentType.Outro, 700, config, isMovie: true));
    }

    [Fact]
    public void IsValidDuration_UnknownType_ReturnsTrue()
    {
        var config = new PluginConfiguration();

        Assert.True(ChapterNameProvider.IsValidDuration((MediaSegmentType)999, 1.0, config, false));
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(120, true)]
    [InlineData(14, false)]
    [InlineData(121, false)]
    public void IsValidDuration_Preview_UsesIntroBounds(double duration, bool expected)
    {
        var config = new PluginConfiguration();

        Assert.Equal(expected, ChapterNameProvider.IsValidDuration(MediaSegmentType.Preview, duration, config, false));
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(600, true)]  // uses outro max, not intro max
    [InlineData(14, false)]
    [InlineData(601, false)]
    public void IsValidDuration_Recap_UsesIntroMinAndOutroMax(double duration, bool expected)
    {
        var config = new PluginConfiguration();

        Assert.Equal(expected, ChapterNameProvider.IsValidDuration(MediaSegmentType.Recap, duration, config, false));
    }
}
