using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Keyframes;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

public class RefinementPipelineTests
{
    private readonly IChapterManager _chapterManager = Substitute.For<IChapterManager>();
    private readonly IKeyframeManager _keyframeManager = Substitute.For<IKeyframeManager>();
    private readonly RefinementPipeline _pipeline;
    private readonly Guid _itemId = Guid.NewGuid();

    public RefinementPipelineTests()
    {
        // SegmentRefiner requires FfmpegBlackFrameService, but silence refinement
        // is disabled when Plugin.Instance is null, so it won't actually be called.
        var blackFrameService = new FfmpegBlackFrameService(
            Substitute.For<IMediaEncoder>(),
            Substitute.For<IConfigurationManager>(),
            NullLogger<FfmpegBlackFrameService>.Instance);

        var refiner = new SegmentRefiner(blackFrameService, NullLogger<SegmentRefiner>.Instance);
        var chapterSnapper = new ChapterSnapper(_chapterManager, NullLogger<ChapterSnapper>.Instance);
        var keyframeSnapper = new KeyframeSnapper(_keyframeManager, NullLogger<KeyframeSnapper>.Instance);

        _pipeline = new RefinementPipeline(refiner, chapterSnapper, keyframeSnapper);
    }

    [Fact]
    public async Task RefineAsync_NoChaptersNoKeyframes_ReturnsOriginal()
    {
        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>());
        _keyframeManager.GetKeyframeData(_itemId).Returns(new List<KeyframeData>());

        var (start, end) = await _pipeline.RefineAsync(
            _itemId,
            10 * TimeSpan.TicksPerSecond,
            60 * TimeSpan.TicksPerSecond,
            "/fake/path.mkv",
            "h264",
            CancellationToken.None);

        Assert.Equal(10 * TimeSpan.TicksPerSecond, start);
        Assert.Equal(60 * TimeSpan.TicksPerSecond, end);
    }

    [Fact]
    public async Task RefineAsync_ChapterSnapping_AdjustsBoundaries()
    {
        var startTicks = 30 * TimeSpan.TicksPerSecond;
        var endTicks = 120 * TimeSpan.TicksPerSecond;
        var chapterAtStart = 31 * TimeSpan.TicksPerSecond;
        var chapterAtEnd = 119 * TimeSpan.TicksPerSecond;

        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>
        {
            new() { StartPositionTicks = chapterAtStart },
            new() { StartPositionTicks = chapterAtEnd }
        });
        _keyframeManager.GetKeyframeData(_itemId).Returns(new List<KeyframeData>());

        var (start, end) = await _pipeline.RefineAsync(
            _itemId, startTicks, endTicks, "/fake/path.mkv", "h264", CancellationToken.None);

        Assert.Equal(chapterAtStart, start);
        Assert.Equal(chapterAtEnd, end);
    }

    [Fact]
    public async Task RefineAsync_KeyframeSnapping_AdjustsBoundaries()
    {
        var startTicks = 30 * TimeSpan.TicksPerSecond;
        var endTicks = 120 * TimeSpan.TicksPerSecond;
        var keyframeBefore = 29 * TimeSpan.TicksPerSecond;
        var keyframeAfter = 121 * TimeSpan.TicksPerSecond;

        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>());
        var keyframeData = new KeyframeData(
            200 * TimeSpan.TicksPerSecond,
            new long[] { keyframeBefore, keyframeAfter });
        _keyframeManager.GetKeyframeData(_itemId)
            .Returns(new List<KeyframeData> { keyframeData });

        var (start, end) = await _pipeline.RefineAsync(
            _itemId, startTicks, endTicks, "/fake/path.mkv", "h264", CancellationToken.None);

        Assert.Equal(keyframeBefore, start);
        Assert.Equal(keyframeAfter, end);
    }

    [Fact]
    public async Task RefineAsync_ChapterAndKeyframe_ChapterAppliedFirst()
    {
        // Chapter at 31s, keyframe at 30.5s — chapter snap happens first (to 31s),
        // then keyframe snap looks near 31s
        var startTicks = 30 * TimeSpan.TicksPerSecond;
        var endTicks = 120 * TimeSpan.TicksPerSecond;
        var chapterTicks = 31 * TimeSpan.TicksPerSecond;
        var keyframeNearChapter = (long)(30.5 * TimeSpan.TicksPerSecond);

        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>
        {
            new() { StartPositionTicks = chapterTicks }
        });

        var keyframeData = new KeyframeData(
            200 * TimeSpan.TicksPerSecond,
            new long[] { keyframeNearChapter, 200 * TimeSpan.TicksPerSecond });
        _keyframeManager.GetKeyframeData(_itemId)
            .Returns(new List<KeyframeData> { keyframeData });

        var (start, _) = await _pipeline.RefineAsync(
            _itemId, startTicks, endTicks, "/fake/path.mkv", "h264", CancellationToken.None);

        // Chapter snaps 30s -> 31s, then keyframe snaps 31s -> 30.5s (keyframe AT OR BEFORE 31s)
        Assert.Equal(keyframeNearChapter, start);
    }
}
