using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

public class ChapterSnapperTests
{
    private readonly IChapterManager _chapterManager = Substitute.For<IChapterManager>();
    private readonly ChapterSnapper _snapper;
    private readonly Guid _itemId = Guid.NewGuid();

    public ChapterSnapperTests()
    {
        _snapper = new ChapterSnapper(
            _chapterManager,
            NullLogger<ChapterSnapper>.Instance);
    }

    [Fact]
    public void SnapsToNearestChapterWithinWindow()
    {
        // Plugin.Instance is null so defaults are used: EnableChapterSnapping=true, window=5.0s
        var targetTicks = 30 * TimeSpan.TicksPerSecond;
        var chapterTicks = (long)(31.5 * TimeSpan.TicksPerSecond);

        _chapterManager.GetChapters(_itemId)
            .Returns(new List<ChapterInfo>
            {
                new() { StartPositionTicks = 0 },
                new() { StartPositionTicks = chapterTicks },
                new() { StartPositionTicks = 60 * TimeSpan.TicksPerSecond }
            });

        var result = _snapper.SnapToChapter(_itemId, targetTicks, CancellationToken.None);

        Assert.Equal(chapterTicks, result);
    }

    [Fact]
    public void ReturnsOriginalWhenNoChapterInWindow()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;

        _chapterManager.GetChapters(_itemId)
            .Returns(new List<ChapterInfo>
            {
                new() { StartPositionTicks = 0 },
                new() { StartPositionTicks = 60 * TimeSpan.TicksPerSecond }
            });

        var result = _snapper.SnapToChapter(_itemId, targetTicks, CancellationToken.None);

        Assert.Equal(targetTicks, result);
    }

    [Fact]
    public void ReturnsOriginalWhenNoChapters()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;

        _chapterManager.GetChapters(_itemId)
            .Returns(new List<ChapterInfo>());

        var result = _snapper.SnapToChapter(_itemId, targetTicks, CancellationToken.None);

        Assert.Equal(targetTicks, result);
    }

    [Fact]
    public void SnapsToClosestOfMultipleChaptersInWindow()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;
        var closerChapter = (long)(29.0 * TimeSpan.TicksPerSecond);
        var fartherChapter = (long)(33.0 * TimeSpan.TicksPerSecond);

        _chapterManager.GetChapters(_itemId)
            .Returns(new List<ChapterInfo>
            {
                new() { StartPositionTicks = closerChapter },
                new() { StartPositionTicks = fartherChapter }
            });

        var result = _snapper.SnapToChapter(_itemId, targetTicks, CancellationToken.None);

        Assert.Equal(closerChapter, result);
    }

    [Fact]
    public void ExactMatchReturnsChapterPosition()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;

        _chapterManager.GetChapters(_itemId)
            .Returns(new List<ChapterInfo>
            {
                new() { StartPositionTicks = targetTicks }
            });

        var result = _snapper.SnapToChapter(_itemId, targetTicks, CancellationToken.None);

        Assert.Equal(targetTicks, result);
    }

    [Fact]
    public void CancellationIsRespected()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _snapper.SnapToChapter(_itemId, 0, cts.Token));
    }
}
