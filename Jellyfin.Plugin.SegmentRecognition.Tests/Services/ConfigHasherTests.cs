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
        Assert.Equal(ConfigHasher.BlackFrameExtraction(config1), ConfigHasher.BlackFrameExtraction(config2));
        Assert.Equal(ConfigHasher.BlackFrameSegments(config1), ConfigHasher.BlackFrameSegments(config2));
    }

    /// <summary>
    /// The intro hash covers settings that change a fingerprint's bytes. How much of the item is
    /// covered is not one of them - that is tracked per fingerprint, so a region change never has
    /// to invalidate fingerprints that are already wide enough.
    /// </summary>
    [Fact]
    public void DifferentSampleRate_ProducesDifferentIntroHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { ChromaprintSampleRate = 44100 };

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
        var config2 = new PluginConfiguration { MinIntroDurationSeconds = 8 };

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
    public void BlackFrameExtraction_AlwaysReturnsSameHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { BlackFrameThreshold = 50.0 };

        // Extraction hash is deliberately constant: re-scanning the whole library because a
        // threshold moved is prohibitively expensive. ReanalyzeBlackFrames is the escape hatch.
        Assert.Equal(ConfigHasher.BlackFrameExtraction(config1), ConfigHasher.BlackFrameExtraction(config2));
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
        var blackFrameExtraction = ConfigHasher.BlackFrameExtraction(config);
        var blackFrameSegments = ConfigHasher.BlackFrameSegments(config);

        // All should be distinct.
        var hashes = new[] { intro, credits, comparison, chapter, blackFrameExtraction, blackFrameSegments };
        Assert.Equal(hashes.Length, new System.Collections.Generic.HashSet<string>(hashes).Count);
    }

    [Fact]
    public void SilenceRefinementToggle_ChangesComparisonHash()
    {
        var config1 = new PluginConfiguration { EnableSilenceRefinement = true };
        var config2 = new PluginConfiguration { EnableSilenceRefinement = false };

        Assert.NotEqual(ConfigHasher.ChromaprintComparison(config1), ConfigHasher.ChromaprintComparison(config2));
    }

    [Fact]
    public void PreviewInferenceToggle_ChangesComparisonHash()
    {
        var config1 = new PluginConfiguration { EnablePreviewInference = false };
        var config2 = new PluginConfiguration { EnablePreviewInference = true };

        Assert.NotEqual(ConfigHasher.ChromaprintComparison(config1), ConfigHasher.ChromaprintComparison(config2));
    }

    [Fact]
    public void PreviewMinDuration_ChangesComparisonHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { MinPreviewDurationSeconds = 5 };

        Assert.NotEqual(ConfigHasher.ChromaprintComparison(config1), ConfigHasher.ChromaprintComparison(config2));
    }

    [Fact]
    public void PreviewMaxDuration_ChangesComparisonHash()
    {
        var config1 = new PluginConfiguration();
        var config2 = new PluginConfiguration { MaxPreviewDurationSeconds = 60 };

        Assert.NotEqual(ConfigHasher.ChromaprintComparison(config1), ConfigHasher.ChromaprintComparison(config2));
    }
}
