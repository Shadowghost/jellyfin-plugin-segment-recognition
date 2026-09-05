using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Tests that every configuration value which changes an analysis result also changes the hash
/// used to detect stale results.
/// </summary>
/// <remarks>
/// Several knobs were documented as "frozen, non-config parameters" while actually being editable
/// settings. Changing them left existing results in place, so the new setting silently applied to
/// newly-analyzed items only - and, for the sample rate, produced fingerprints that were compared
/// against incompatible cached ones.
/// </remarks>
public sealed class ConfigHasherCoverageTests
{
    [Fact]
    public void SampleRate_ChangesIntroFingerprintHash()
    {
        Assert.NotEqual(
            ConfigHasher.ChromaprintIntro(new PluginConfiguration()),
            ConfigHasher.ChromaprintIntro(new PluginConfiguration { ChromaprintSampleRate = 44100 }));
    }

    [Fact]
    public void SampleRate_ChangesCreditsFingerprintHash()
    {
        Assert.NotEqual(
            ConfigHasher.ChromaprintCredits(new PluginConfiguration()),
            ConfigHasher.ChromaprintCredits(new PluginConfiguration { ChromaprintSampleRate = 44100 }));
    }

    /// <summary>
    /// Upgrade guard. Fingerprint extraction is the most expensive thing the plugin does, so the
    /// default-configuration hashes are frozen against the values shipped before the sample rate
    /// became reachable from the UI. If these literals ever need changing, every fingerprint in
    /// every existing library is re-extracted - make that a deliberate decision, not a side effect.
    /// </summary>
    [Fact]
    public void DefaultConfigFingerprintHashes_AreUnchangedFromTheShippedValues()
    {
        var config = new PluginConfiguration();

        // SHA-256 of "cp-intro|iap=0.25|cads=600" and "cp-credits|cads=240|pad=True", the exact
        // inputs the previous release hashed for a default configuration.
        Assert.Equal("9259B22D30D2E3D9", ConfigHasher.ChromaprintIntro(config));
        Assert.Equal("6CED4DE3B3FA6EEF", ConfigHasher.ChromaprintCredits(config));
    }

    [Fact]
    public void MaxBitErrors_ChangesComparisonHash()
    {
        Assert.NotEqual(
            ConfigHasher.ChromaprintComparison(new PluginConfiguration()),
            ConfigHasher.ChromaprintComparison(new PluginConfiguration { ChromaprintMaxBitErrors = 10 }));
    }

    [Fact]
    public void MaxTimeSkip_ChangesComparisonHash()
    {
        Assert.NotEqual(
            ConfigHasher.ChromaprintComparison(new PluginConfiguration()),
            ConfigHasher.ChromaprintComparison(new PluginConfiguration { ChromaprintMaxTimeSkipSeconds = 8.0 }));
    }

    [Fact]
    public void InvertedIndexShift_ChangesComparisonHash()
    {
        Assert.NotEqual(
            ConfigHasher.ChromaprintComparison(new PluginConfiguration()),
            ConfigHasher.ChromaprintComparison(new PluginConfiguration { ChromaprintInvertedIndexShift = 4 }));
    }

    [Theory]
    [InlineData("BlackFrameMinDurationMs")]
    [InlineData("MinIntroDurationSeconds")]
    [InlineData("MaxIntroDurationSeconds")]
    [InlineData("MinOutroDurationSeconds")]
    [InlineData("MaxOutroDurationSeconds")]
    [InlineData("MaxMovieOutroDurationSeconds")]
    [InlineData("OutroAnalysisSeconds")]
    [InlineData("MinPreviewDurationSeconds")]
    [InlineData("MaxPreviewDurationSeconds")]
    public void SegmentShapingSettings_ChangeBlackFrameSegmentHash(string propertyName)
    {
        var baseline = new PluginConfiguration();
        var changed = new PluginConfiguration();

        var property = typeof(PluginConfiguration).GetProperty(propertyName);
        Assert.NotNull(property);
        property!.SetValue(changed, (int)property.GetValue(baseline)! + 7);

        Assert.NotEqual(
            ConfigHasher.BlackFrameSegments(baseline),
            ConfigHasher.BlackFrameSegments(changed));
    }

    [Fact]
    public void RefinementSettings_ChangeBlackFrameSegmentHash()
    {
        Assert.NotEqual(
            ConfigHasher.BlackFrameSegments(new PluginConfiguration()),
            ConfigHasher.BlackFrameSegments(new PluginConfiguration { EnableChapterSnapping = false }));

        Assert.NotEqual(
            ConfigHasher.BlackFrameSegments(new PluginConfiguration()),
            ConfigHasher.BlackFrameSegments(new PluginConfiguration { KeyframeSnapWindowSeconds = 9.0 }));
    }

    /// <summary>
    /// Chapter snapping settings feed the refinement pipeline both providers share, so a change
    /// must invalidate both hashes - they used to be spelled out separately and could drift.
    /// </summary>
    [Fact]
    public void RefinementSettings_ChangeBothProviderHashes()
    {
        var baseline = new PluginConfiguration();
        var changed = new PluginConfiguration { ChapterSnapWindowSeconds = 12.5 };

        Assert.NotEqual(ConfigHasher.BlackFrameSegments(baseline), ConfigHasher.BlackFrameSegments(changed));
        Assert.NotEqual(ConfigHasher.ChromaprintComparison(baseline), ConfigHasher.ChromaprintComparison(changed));
    }

    /// <summary>
    /// Extraction cost is why this one stays constant; the guarantee is explicit so a future edit
    /// that accidentally makes it config-sensitive fails here rather than triggering a
    /// library-wide re-scan in production.
    /// </summary>
    [Theory]
    [InlineData(720)]
    [InlineData(0)]
    public void BlackFrameExtractionHash_IgnoresExtractionSettings(int analysisHeight)
    {
        Assert.Equal(
            ConfigHasher.BlackFrameExtraction(new PluginConfiguration()),
            ConfigHasher.BlackFrameExtraction(new PluginConfiguration { BlackFrameAnalysisHeight = analysisHeight }));
    }

    [Fact]
    public void ExtractionAndSegmentHashes_AreDistinct()
    {
        var config = new PluginConfiguration();

        Assert.NotEqual(ConfigHasher.BlackFrameExtraction(config), ConfigHasher.BlackFrameSegments(config));
    }
}
