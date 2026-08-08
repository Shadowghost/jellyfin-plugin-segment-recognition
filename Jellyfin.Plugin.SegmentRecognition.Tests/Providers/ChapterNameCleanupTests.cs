using System.Linq;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Guards the <see cref="ChapterNameProvider.ForeignSentinels"/> list used by the cleanup
/// filter. A previous regression let chapter-config changes wipe chromaprint-derived
/// segments because the filter was missing.
/// </summary>
public sealed class ChapterNameCleanupTests
{
    [Theory]
    [InlineData("chromaprint")]
    [InlineData("chromaprint-credits")]
    [InlineData("chromaprint-preview")]
    [InlineData("blackframe-intro")]
    [InlineData("blackframe-outro")]
    [InlineData("blackframe-preview")]
    [InlineData("edl-import")]
    [InlineData("intro-skipper import")]
    public void ForeignSentinels_ContainsAllSiblingSources(string sentinel)
    {
        Assert.Contains(sentinel, ChapterNameProvider.ForeignSentinels);
    }

    [Theory]
    [InlineData("Opening")]
    [InlineData("Ending")]
    [InlineData("OP")]
    [InlineData("ED")]
    [InlineData("Recap")]
    [InlineData("Preview")]
    [InlineData("")]
    public void ForeignSentinels_DoesNotContainNaturalChapterNames(string name)
    {
        Assert.DoesNotContain(name, ChapterNameProvider.ForeignSentinels);
    }

    [Fact]
    public void ForeignSentinels_HasExpectedCount()
    {
        // Bump this when a new sentinel is introduced; the test flags forgotten updates.
        Assert.Equal(8, ChapterNameProvider.ForeignSentinels.Length);
    }

    /// <summary>
    /// The cleanup filter and the serve filter must agree. When they drifted, black-frame preview
    /// and intro-skipper rows were excluded from cleanup but not from ChapterNameProvider's query,
    /// so two providers served the same segment.
    /// </summary>
    [Fact]
    public void ForeignSentinels_IsTheSharedList()
    {
        Assert.Same(SegmentSourceNames.ForeignToChapterName, ChapterNameProvider.ForeignSentinels);
    }

    [Fact]
    public void ForeignSentinels_CoversEveryProviderOwnedSentinel()
    {
        foreach (var owned in SegmentSourceNames.BlackFrameOwned.Concat(SegmentSourceNames.ChromaprintOwned))
        {
            Assert.Contains(owned, ChapterNameProvider.ForeignSentinels);
        }
    }

    [Fact]
    public void ForeignSentinels_HasNoDuplicates()
    {
        Assert.Equal(
            ChapterNameProvider.ForeignSentinels.Length,
            ChapterNameProvider.ForeignSentinels.Distinct().Count());
    }

    [Fact]
    public void ForeignSentinels_AllLowercase()
    {
        foreach (var s in ChapterNameProvider.ForeignSentinels)
        {
            Assert.Equal(s.ToLowerInvariant(), s);
        }
    }
}
