using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

public class ChapterNameMatchingTests
{
    private static bool AnyMatches(System.Text.RegularExpressions.Regex[] regexes, string input)
        => regexes.Any(r => r.IsMatch(input));

    // --- BuildRegexes ---

    [Fact]
    public void BuildRegexes_DefaultConfig_CreatesAllFiveTypes()
    {
        var config = new PluginConfiguration();

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.Contains(MediaSegmentType.Intro, regexes.Keys);
        Assert.Contains(MediaSegmentType.Outro, regexes.Keys);
        Assert.Contains(MediaSegmentType.Recap, regexes.Keys);
        Assert.Contains(MediaSegmentType.Preview, regexes.Keys);
        Assert.Contains(MediaSegmentType.Commercial, regexes.Keys);
    }

    [Fact]
    public void BuildRegexes_OneRegexPerPattern()
    {
        var config = new PluginConfiguration
        {
            IntroChapterNames = ["Intro", "Opening", "OP"],
            OutroChapterNames = [],
            RecapChapterNames = [],
            PreviewChapterNames = [],
            CommercialChapterNames = []
        };

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.Equal(3, regexes[MediaSegmentType.Intro].Length);
    }

    [Fact]
    public void BuildRegexes_EmptyPatterns_OmitsType()
    {
        var config = new PluginConfiguration
        {
            IntroChapterNames = [],
            OutroChapterNames = ["Credits"],
            RecapChapterNames = [],
            PreviewChapterNames = [],
            CommercialChapterNames = []
        };

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.Single(regexes);
        Assert.Contains(MediaSegmentType.Outro, regexes.Keys);
    }

    [Fact]
    public void BuildRegexes_CommercialPatterns_MatchExpectedNames()
    {
        var config = new PluginConfiguration();

        var regexes = ChapterNameProvider.BuildRegexes(config);
        var commercial = regexes[MediaSegmentType.Commercial];

        Assert.True(AnyMatches(commercial, "Commercial"));
        Assert.True(AnyMatches(commercial, "Ad break"));
        Assert.True(AnyMatches(commercial, "Werbung"));
        Assert.True(AnyMatches(commercial, "Publicidad"));
        Assert.False(AnyMatches(commercial, "Opening"));
    }

    [Fact]
    public void IsValidDuration_Commercial_RespectsCommercialBounds()
    {
        var config = new PluginConfiguration
        {
            MinCommercialDurationSeconds = 5,
            MaxCommercialDurationSeconds = 180
        };

        Assert.False(ChapterNameProvider.IsValidDuration(MediaSegmentType.Commercial, 3, config, isMovie: false));
        Assert.True(ChapterNameProvider.IsValidDuration(MediaSegmentType.Commercial, 30, config, isMovie: false));
        Assert.True(ChapterNameProvider.IsValidDuration(MediaSegmentType.Commercial, 180, config, isMovie: false));
        Assert.False(ChapterNameProvider.IsValidDuration(MediaSegmentType.Commercial, 300, config, isMovie: false));
    }

    [Fact]
    public void BuildRegexes_AllEmpty_ReturnsEmptyDictionary()
    {
        var config = new PluginConfiguration
        {
            IntroChapterNames = [],
            OutroChapterNames = [],
            RecapChapterNames = [],
            PreviewChapterNames = [],
            CommercialChapterNames = []
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

        Assert.True(AnyMatches(regexes[MediaSegmentType.Intro], chapterName));
    }

    [Theory]
    [InlineData("Outro")]
    [InlineData("Credits")]
    [InlineData("End Credits")]
    [InlineData("Ending")]
    public void BuildRegexes_DefaultConfig_MatchesOutroPatterns(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.True(AnyMatches(regexes[MediaSegmentType.Outro], chapterName));
    }

    [Theory]
    [InlineData("Recap")]
    [InlineData("Previously on")]
    public void BuildRegexes_DefaultConfig_MatchesRecapPatterns(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.True(AnyMatches(regexes[MediaSegmentType.Recap], chapterName));
    }

    [Theory]
    [InlineData("Preview")]
    [InlineData("Next time")]
    [InlineData("Next Episode")]
    public void BuildRegexes_DefaultConfig_MatchesPreviewPatterns(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.True(AnyMatches(regexes[MediaSegmentType.Preview], chapterName));
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

        Assert.False(AnyMatches(regexes[MediaSegmentType.Intro], chapterName));
    }

    [Fact]
    public void BuildRegexes_MatchesPatternAfterSpace()
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.True(AnyMatches(regexes[MediaSegmentType.Intro], "Chapter 1: Intro"));
    }

    [Fact]
    public void BuildRegexes_MatchesPatternWithColon()
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.True(AnyMatches(regexes[MediaSegmentType.Intro], "Intro:"));
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

        Assert.True(AnyMatches(regexes[MediaSegmentType.Intro], "Opening (credits)"));
        // Without escaping, "(credits)" would be a regex group, not a literal match
        Assert.False(AnyMatches(regexes[MediaSegmentType.Intro], "Opening credits"));
    }

    [Fact]
    public void BuildRegexes_NegativeLookahead_RejectsPatternFollowedByEnd()
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.False(AnyMatches(regexes[MediaSegmentType.Intro], "Intro End"));
    }

    [Theory]
    [InlineData("Intro: End", MediaSegmentType.Intro)]
    [InlineData("Credits: End", MediaSegmentType.Outro)]
    [InlineData("Preview: End", MediaSegmentType.Preview)]
    [InlineData("Recap: End", MediaSegmentType.Recap)]
    [InlineData("Commercial: End", MediaSegmentType.Commercial)]
    public void BuildRegexes_NegativeLookahead_RejectsColonDelimitedEndLabels(string chapterName, MediaSegmentType type)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.False(AnyMatches(regexes[type], chapterName));
    }

    [Theory]
    [InlineData("Intro: Endgame")]  // "Endgame" is not the "End" marker word
    [InlineData("Intro: Ending")]
    [InlineData("Intro Endeavour")]
    public void BuildRegexes_EndMarkerLookahead_AllowsWordsMerelyStartingWithEnd(string chapterName)
    {
        // Single-keyword config isolates the lookahead from competing keywords like "Ending".
        var config = OnlyIntro("Intro");

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.True(AnyMatches(regexes[MediaSegmentType.Intro], chapterName));
    }

    // --- Punctuation-delimited word boundaries ---

    [Theory]
    [InlineData("(Intro)")]
    [InlineData("[Intro]")]
    [InlineData("Intro.")]
    [InlineData("Recap/Intro")]
    [InlineData("Intro, part 1")]
    public void BuildRegexes_MatchesPunctuationDelimitedKeywords(string chapterName)
    {
        var config = OnlyIntro("Intro");

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.True(AnyMatches(regexes[MediaSegmentType.Intro], chapterName));
    }

    [Theory]
    [InlineData("Introvert")]
    [InlineData("Introspection")]
    [InlineData("Reintroduce")]
    public void BuildRegexes_WidenedBoundaries_StillRejectSubstrings(string chapterName)
    {
        var config = OnlyIntro("Intro");

        var regexes = ChapterNameProvider.BuildRegexes(config);

        Assert.False(AnyMatches(regexes[MediaSegmentType.Intro], chapterName));
    }

    // --- Match precedence (longest keyword wins) ---

    [Fact]
    public void MatchChapterName_PrefersLongestKeyword_AcrossTypes()
    {
        var config = new PluginConfiguration
        {
            IntroChapterNames = ["Intro"],
            OutroChapterNames = ["Intro Credits"],
            RecapChapterNames = [],
            PreviewChapterNames = [],
            CommercialChapterNames = []
        };

        var regexes = ChapterNameProvider.BuildRegexes(config);

        // "Intro Credits" matches the Intro keyword "Intro" (len 5) and the Outro keyword
        // "Intro Credits" (len 13); the longer, more specific Outro keyword wins even though
        // Intro is registered first.
        Assert.Equal(MediaSegmentType.Outro, ChapterNameProvider.MatchChapterName(regexes, "Intro Credits"));
    }

    [Fact]
    public void MatchChapterName_TieBreaksByEarliestPosition()
    {
        var config = new PluginConfiguration
        {
            IntroChapterNames = ["Opening"],
            OutroChapterNames = ["Credits"],
            RecapChapterNames = [],
            PreviewChapterNames = [],
            CommercialChapterNames = []
        };

        var regexes = ChapterNameProvider.BuildRegexes(config);

        // "Opening" and "Credits" are equal-length matches; "Opening" appears first, so it wins.
        Assert.Equal(MediaSegmentType.Intro, ChapterNameProvider.MatchChapterName(regexes, "Opening Credits"));
        // Reversed order flips the winner - resolution follows position, not registration order
        // (Intro is registered before Outro, yet Outro wins here because "Credits" comes first).
        Assert.Equal(MediaSegmentType.Outro, ChapterNameProvider.MatchChapterName(regexes, "Credits, then Opening"));
    }

    // --- Added default synonyms ---

    [Theory]
    [InlineData("PV")]
    [InlineData("Teaser")]
    [InlineData("Trailer")]
    [InlineData("Sneak Peek")]
    [InlineData("Coming Up")]
    [InlineData("Coming Soon")]
    [InlineData("Next on")]
    public void BuildRegexes_DefaultConfig_MatchesAddedPreviewSynonyms(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.True(AnyMatches(regexes[MediaSegmentType.Preview], chapterName));
    }

    [Theory]
    [InlineData("Summary")]
    [InlineData("Previously")]
    [InlineData("Last time")]
    [InlineData("Catch up")]
    [InlineData("Catch-up")]
    public void BuildRegexes_DefaultConfig_MatchesAddedRecapSynonyms(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.True(AnyMatches(regexes[MediaSegmentType.Recap], chapterName));
    }

    private static PluginConfiguration OnlyIntro(params string[] introNames) => new()
    {
        IntroChapterNames = introNames,
        OutroChapterNames = [],
        RecapChapterNames = [],
        PreviewChapterNames = [],
        CommercialChapterNames = []
    };

    // --- yt-dlp SponsorBlock chapter recognition (via default chapter-name lists) ---

    [Theory]
    [InlineData("[SponsorBlock]: Sponsor", MediaSegmentType.Commercial)]
    [InlineData("[SponsorBlock]: Unpaid/Self Promotion", MediaSegmentType.Commercial)]
    [InlineData("[SponsorBlock]: selfpromo", MediaSegmentType.Commercial)]
    [InlineData("[SponsorBlock]: sponsor", MediaSegmentType.Commercial)]
    [InlineData("[SponsorBlock]: Intermission/Intro Animation", MediaSegmentType.Intro)]
    [InlineData("[SponsorBlock]: intro", MediaSegmentType.Intro)]
    [InlineData("[SponsorBlock]: Endcards/Credits", MediaSegmentType.Outro)]
    [InlineData("[SponsorBlock]: outro", MediaSegmentType.Outro)]
    [InlineData("[SponsorBlock]: Preview/Recap", MediaSegmentType.Recap)]
    public void DefaultConfig_RecognizesSponsorBlockChapter(string chapterName, MediaSegmentType expected)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        var matched = ChapterNameProvider.MatchChapterName(regexes, chapterName);

        Assert.Equal(expected, matched);
    }

    [Theory]
    // Categories without a clean segment-type analogue should not match anything in the
    // defaults - users can opt in by adding their own entries.
    [InlineData("[SponsorBlock]: Filler Tangent")]
    [InlineData("[SponsorBlock]: filler")]
    [InlineData("[SponsorBlock]: interaction")]
    [InlineData("[SponsorBlock]: music_offtopic")]
    [InlineData("[SponsorBlock]: poi_highlight")]
    public void DefaultConfig_IgnoresUnmappedSponsorBlockCategories(string chapterName)
    {
        var regexes = ChapterNameProvider.BuildRegexes(new PluginConfiguration());

        Assert.Null(ChapterNameProvider.MatchChapterName(regexes, chapterName));
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
    [InlineData(5, true)]    // exact min (default MinIntroDurationSeconds)
    [InlineData(60, true)]   // mid-range
    [InlineData(240, true)]  // exact max (default MaxIntroDurationSeconds)
    [InlineData(4, false)]   // below min
    [InlineData(241, false)] // above max
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
    [InlineData(5, true)]    // exact min (default MinIntroDurationSeconds)
    [InlineData(240, true)]  // exact max (default MaxIntroDurationSeconds)
    [InlineData(4, false)]
    [InlineData(241, false)]
    public void IsValidDuration_Preview_UsesIntroBounds(double duration, bool expected)
    {
        var config = new PluginConfiguration();

        Assert.Equal(expected, ChapterNameProvider.IsValidDuration(MediaSegmentType.Preview, duration, config, false));
    }

    [Theory]
    [InlineData(5, true)]    // exact min (default MinIntroDurationSeconds)
    [InlineData(600, true)]  // uses outro max, not intro max
    [InlineData(4, false)]
    [InlineData(601, false)]
    public void IsValidDuration_Recap_UsesIntroMinAndOutroMax(double duration, bool expected)
    {
        var config = new PluginConfiguration();

        Assert.Equal(expected, ChapterNameProvider.IsValidDuration(MediaSegmentType.Recap, duration, config, false));
    }

}
