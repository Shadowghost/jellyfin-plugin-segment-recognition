using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests for joining same-type chapter segments that touch.
/// </summary>
/// <remarks>
/// Chapters are contiguous by construction, so a title sequence split across an "Introduction" and
/// an "OP" chapter produced two Intro segments meeting exactly at one tick. A player asking for the
/// intro then got two skip targets for one opening, or skipped only the first half.
/// </remarks>
public sealed class ChapterNameMergeTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    private static readonly PluginConfiguration _config = new();

    private static ChapterAnalysisResult Segment(MediaSegmentType type, double startSeconds, double endSeconds, string name)
        => new()
        {
            ItemId = Guid.Empty,
            SegmentType = (int)type,
            StartTicks = (long)(startSeconds * Second),
            EndTicks = (long)(endSeconds * Second),
            MatchedChapterName = name,
            ConfigHash = "h",
            CreatedAt = DateTime.UtcNow,
        };

    private static List<ChapterAnalysisResult> Merge(params ChapterAnalysisResult[] segments)
        => ChapterNameProvider.MergeAdjacent(segments, _config, isMovie: false)
            .OrderBy(s => s.SegmentType).ThenBy(s => s.StartTicks).ToList();

    /// <summary>The real case: "Introduction" 0-140s followed by "OP" 140-230s.</summary>
    [Fact]
    public void TouchingSameTypeSegments_AreJoined()
    {
        var merged = Merge(
            Segment(MediaSegmentType.Intro, 0, 140, "Introduction"),
            Segment(MediaSegmentType.Intro, 140, 230, "OP"));

        var one = Assert.Single(merged);
        Assert.Equal(0, one.StartTicks);
        Assert.Equal(230 * Second, one.EndTicks);
        Assert.Equal("Introduction + OP", one.MatchedChapterName);
    }

    [Fact]
    public void OverlappingSameTypeSegments_AreJoined()
    {
        var merged = Merge(
            Segment(MediaSegmentType.Intro, 0, 100, "A"),
            Segment(MediaSegmentType.Intro, 90, 150, "B"));

        var one = Assert.Single(merged);
        Assert.Equal(150 * Second, one.EndTicks);
    }

    /// <summary>
    /// Same-type segments genuinely recur apart from each other, so a gap means two segments.
    /// </summary>
    [Fact]
    public void SegmentsWithAGap_AreLeftAlone()
    {
        var merged = Merge(
            Segment(MediaSegmentType.Commercial, 300, 360, "Ad break"),
            Segment(MediaSegmentType.Commercial, 900, 960, "Ad break"));

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void DifferentTypesAreNeverJoined()
    {
        var merged = Merge(
            Segment(MediaSegmentType.Intro, 0, 90, "OP"),
            Segment(MediaSegmentType.Recap, 90, 150, "Previously"));

        Assert.Equal(2, merged.Count);
        Assert.Equal("OP", merged.Single(s => s.SegmentType == (int)MediaSegmentType.Intro).MatchedChapterName);
    }

    /// <summary>
    /// A join must not manufacture a segment the duration validator would have rejected. Two
    /// touching 130 s intros total 260 s, past the 240 s cap, so they stay separate.
    /// </summary>
    [Fact]
    public void JoinThatWouldBreachTheDurationCap_IsAbandoned()
    {
        var merged = Merge(
            Segment(MediaSegmentType.Intro, 0, 130, "Part 1"),
            Segment(MediaSegmentType.Intro, 130, 260, "Part 2"));

        Assert.Equal(2, merged.Count);
        Assert.Equal("Part 1", merged[0].MatchedChapterName);
        Assert.Equal("Part 2", merged[1].MatchedChapterName);
    }

    /// <summary>Three touching parts collapse into one.</summary>
    [Fact]
    public void RunsLongerThanTwo_CollapseEntirely()
    {
        var merged = Merge(
            Segment(MediaSegmentType.Intro, 0, 30, "A"),
            Segment(MediaSegmentType.Intro, 30, 60, "B"),
            Segment(MediaSegmentType.Intro, 60, 90, "C"));

        var one = Assert.Single(merged);
        Assert.Equal(90 * Second, one.EndTicks);
        Assert.Equal("A + B + C", one.MatchedChapterName);
    }

    [Fact]
    public void SingleSegment_IsUnchanged()
    {
        var one = Assert.Single(Merge(Segment(MediaSegmentType.Intro, 10, 100, "OP")));
        Assert.Equal("OP", one.MatchedChapterName);
        Assert.Equal(10 * Second, one.StartTicks);
    }

    [Fact]
    public void EmptyInput_ProducesNothing()
    {
        Assert.Empty(ChapterNameProvider.MergeAdjacent([], _config, isMovie: false));
    }

    /// <summary>Input order must not change the outcome.</summary>
    [Fact]
    public void ResultIsIndependentOfInputOrder()
    {
        var forward = Merge(
            Segment(MediaSegmentType.Intro, 0, 140, "Introduction"),
            Segment(MediaSegmentType.Intro, 140, 230, "OP"));
        var reversed = Merge(
            Segment(MediaSegmentType.Intro, 140, 230, "OP"),
            Segment(MediaSegmentType.Intro, 0, 140, "Introduction"));

        Assert.Equal(forward.Single().EndTicks, reversed.Single().EndTicks);
        Assert.Equal(forward.Single().MatchedChapterName, reversed.Single().MatchedChapterName);
    }
}
