using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

public class ConfigHasherTests
{
    [Fact]
    public void SameConfig_ProducesSameHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration();

        Assert.Equal(ConfigHasher.ChromaprintIntro(config1), ConfigHasher.ChromaprintIntro(config2));
        Assert.Equal(ConfigHasher.ChromaprintCredits(config1), ConfigHasher.ChromaprintCredits(config2));
        Assert.Equal(ConfigHasher.ChromaprintComparison(config1), ConfigHasher.ChromaprintComparison(config2));
        Assert.Equal(ConfigHasher.ChapterName(config1), ConfigHasher.ChapterName(config2));
        Assert.Equal(ConfigHasher.BlackFrame(config1), ConfigHasher.BlackFrame(config2));
    }

    [Fact]
    public void DifferentIntroConfig_ProducesDifferentHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { IntroAnalysisPercent = 0.5 };

        Assert.NotEqual(ConfigHasher.ChromaprintIntro(config1), ConfigHasher.ChromaprintIntro(config2));
    }

    [Fact]
    public void DifferentCreditsConfig_ProducesDifferentHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { CreditsAnalysisDurationSeconds = 999 };

        Assert.NotEqual(ConfigHasher.ChromaprintCredits(config1), ConfigHasher.ChromaprintCredits(config2));
    }

    [Fact]
    public void DifferentComparisonConfig_ProducesDifferentHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { ChromaprintMinMatchDurationSeconds = 30 };

        Assert.NotEqual(ConfigHasher.ChromaprintComparison(config1), ConfigHasher.ChromaprintComparison(config2));
    }

    [Fact]
    public void DifferentChapterNames_ProducesDifferentHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { IntroChapterNames = ["CustomIntro"] };

        Assert.NotEqual(ConfigHasher.ChapterName(config1), ConfigHasher.ChapterName(config2));
    }

    [Fact]
    public void BlackFrame_AlwaysReturnsSameHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { BlackFrameThreshold = 50.0 };

        // BlackFrame hash is a constant — config changes don't affect it.
        Assert.Equal(ConfigHasher.BlackFrame(config1), ConfigHasher.BlackFrame(config2));
    }

    [Fact]
    public void HashesAre16CharHex()
    {
        var config = new PluginConfiguration();

        var hash = ConfigHasher.ChromaprintIntro(config);

        Assert.Equal(16, hash.Length);
        Assert.Matches("^[0-9A-F]{16}$", hash);
    }

    [Fact]
    public void DifferentMethodsProduceDifferentHashes()
    {
        var config = new PluginConfiguration();

        var intro = ConfigHasher.ChromaprintIntro(config);
        var credits = ConfigHasher.ChromaprintCredits(config);
        var comparison = ConfigHasher.ChromaprintComparison(config);
        var chapter = ConfigHasher.ChapterName(config);
        var blackFrame = ConfigHasher.BlackFrame(config);

        // All should be distinct.
        var hashes = new[] { intro, credits, comparison, chapter, blackFrame };
        Assert.Equal(hashes.Length, new System.Collections.Generic.HashSet<string>(hashes).Count);
    }

    [Fact]
    public void SilenceRefinementToggle_ChangesComparisonHash()
    {
        var config1 = new PluginConfiguration { EnableSilenceRefinement = true };
        var config2 = new PluginConfiguration { EnableSilenceRefinement = false };

        Assert.NotEqual(ConfigHasher.ChromaprintComparison(config1), ConfigHasher.ChromaprintComparison(config2));
    }
}
