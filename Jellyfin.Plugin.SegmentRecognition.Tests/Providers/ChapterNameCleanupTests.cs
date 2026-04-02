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
    [InlineData("blackframe-preview")]
    [InlineData("edl-import")]
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
        Assert.Equal(5, ChapterNameProvider.ForeignSentinels.Length);
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
