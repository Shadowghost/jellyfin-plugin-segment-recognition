using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Keyframes;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Tests that the refinement pipeline never emits a degenerate range.
/// </summary>
/// <remarks>
/// Each stage snaps the two boundaries independently, so no single stage is wrong when both land
/// on the same chapter marker - but the combined result is a zero-length or inverted segment,
/// which is worse than leaving the boundaries unrefined.
/// </remarks>
[Collection(PluginStateCollection.Name)]
public sealed class RefinementPipelineGuardTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    private readonly IChapterManager _chapterManager = Substitute.For<IChapterManager>();
    private readonly IKeyframeManager _keyframeManager = Substitute.For<IKeyframeManager>();
    private readonly FfmpegBlackFrameService _blackFrameService;
    private readonly Guid _itemId = Guid.NewGuid();

    public RefinementPipelineGuardTests()
    {
        _blackFrameService = Substitute.ForPartsOf<FfmpegBlackFrameService>(
            Substitute.For<IMediaEncoder>(),
            Substitute.For<IConfigurationManager>(),
            NullLogger<FfmpegBlackFrameService>.Instance);

        _blackFrameService.DetectSilenceAsync(
                Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(),
                Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<(double, double)>());

        _keyframeManager.GetKeyframeData(_itemId).Returns(new List<KeyframeData>().AsReadOnly());
        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>());
    }

    private RefinementPipeline CreatePipeline() => new(
        new SegmentRefiner(_blackFrameService, NullLogger<SegmentRefiner>.Instance),
        new ChapterSnapper(_chapterManager, NullLogger<ChapterSnapper>.Instance),
        new KeyframeSnapper(_keyframeManager, NullLogger<KeyframeSnapper>.Instance));

    /// <summary>
    /// Both boundaries of a short segment fall within the snap window of one chapter marker, so
    /// each independently snaps onto it and the segment collapses to zero length.
    /// </summary>
    [Fact]
    public async Task BothBoundariesSnappingToOneChapter_KeepsOriginalRange()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = true;
            c.ChapterSnapWindowSeconds = 30;
            c.EnableKeyframeSnapping = false;
        });

        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>
        {
            new() { Name = "Marker", StartPositionTicks = 100 * Second },
        });

        var (start, end) = await CreatePipeline().RefineAsync(
            _itemId, 95 * Second, 105 * Second, "/media/test.mkv", null, CancellationToken.None);

        Assert.True(start < end, $"expected a non-degenerate range, got {start}..{end}");
        Assert.Equal(95 * Second, start);
        Assert.Equal(105 * Second, end);
    }

    [Fact]
    public async Task NormalChapterSnapping_StillApplies()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = true;
            c.ChapterSnapWindowSeconds = 5;
            c.EnableKeyframeSnapping = false;
        });

        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>
        {
            new() { Name = "Start", StartPositionTicks = 100 * Second },
            new() { Name = "End", StartPositionTicks = 200 * Second },
        });

        var (start, end) = await CreatePipeline().RefineAsync(
            _itemId, 102 * Second, 198 * Second, "/media/test.mkv", null, CancellationToken.None);

        Assert.Equal(100 * Second, start);
        Assert.Equal(200 * Second, end);
    }

    [Fact]
    public async Task WithAllStagesDisabled_BoundariesArePassedThrough()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
        });

        var (start, end) = await CreatePipeline().RefineAsync(
            _itemId, 10 * Second, 20 * Second, "/media/test.mkv", null, CancellationToken.None);

        Assert.Equal(10 * Second, start);
        Assert.Equal(20 * Second, end);
    }

    /// <summary>
    /// Keyframe snapping widens (start goes back, end goes forward), so it cannot invert a range
    /// on its own - but the guard must not undo its legitimate effect either.
    /// </summary>
    [Fact]
    public async Task KeyframeSnappingWidensTheRange()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = true;
            c.KeyframeSnapWindowSeconds = 10;
        });

        _keyframeManager.GetKeyframeData(_itemId).Returns(new List<KeyframeData>
        {
            new(0, [90 * Second, 100 * Second, 200 * Second, 210 * Second]),
        }.AsReadOnly());

        var (start, end) = await CreatePipeline().RefineAsync(
            _itemId, 105 * Second, 195 * Second, "/media/test.mkv", null, CancellationToken.None);

        Assert.Equal(100 * Second, start);
        Assert.Equal(200 * Second, end);
    }

    /// <summary>
    /// Checking only for inversion was not enough. Both boundaries move inward independently, so a
    /// segment that qualified at its window's minimum can survive as a fraction of a second - a
    /// positive range, and a useless one. On a real library this stored 199 intros below the 5 s
    /// minimum, the shortest at 0.01 s.
    /// </summary>
    [Fact]
    public async Task RefinementThatShrinksBelowTheMinimum_KeepsOriginalRange()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = true;
            c.ChapterSnapWindowSeconds = 5;
            c.EnableKeyframeSnapping = false;
        });

        // Two markers 1.5 s apart inside a 10 s segment: each boundary snaps to its nearer one,
        // leaving a positive but meaningless range.
        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>
        {
            new() { Name = "A", StartPositionTicks = 104 * Second },
            new() { Name = "B", StartPositionTicks = (long)(105.5 * Second) },
        });

        var (start, end) = await CreatePipeline().RefineAsync(
            _itemId, 100 * Second, 110 * Second, "/media/test.mkv", null, CancellationToken.None,
            minDurationSeconds: 5);

        Assert.Equal(100 * Second, start);
        Assert.Equal(110 * Second, end);
    }

    /// <summary>
    /// The floor only rejects; refinement that stays above it still applies.
    /// </summary>
    [Fact]
    public async Task RefinementThatStaysAboveTheMinimum_IsKept()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = true;
            c.ChapterSnapWindowSeconds = 5;
            c.EnableKeyframeSnapping = false;
        });

        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>
        {
            new() { Name = "Start", StartPositionTicks = 100 * Second },
            new() { Name = "End", StartPositionTicks = 200 * Second },
        });

        var (start, end) = await CreatePipeline().RefineAsync(
            _itemId, 102 * Second, 198 * Second, "/media/test.mkv", null, CancellationToken.None,
            minDurationSeconds: 5);

        Assert.Equal(100 * Second, start);
        Assert.Equal(200 * Second, end);
    }

    /// <summary>
    /// Callers that pass no minimum keep the previous behaviour - only inversion is rejected.
    /// </summary>
    [Fact]
    public async Task WithoutAMinimum_ShrinkageIsStillAllowed()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = true;
            c.ChapterSnapWindowSeconds = 5;
            c.EnableKeyframeSnapping = false;
        });

        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>
        {
            new() { Name = "A", StartPositionTicks = 104 * Second },
            new() { Name = "B", StartPositionTicks = (long)(105.5 * Second) },
        });

        var (start, end) = await CreatePipeline().RefineAsync(
            _itemId, 100 * Second, 110 * Second, "/media/test.mkv", null, CancellationToken.None);

        Assert.Equal(104 * Second, start);
        Assert.Equal((long)(105.5 * Second), end);
    }
}
