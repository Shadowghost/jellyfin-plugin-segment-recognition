using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.MediaEncoding.Keyframes;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Controller.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

public class KeyframeSnapperTests
{
    private readonly IKeyframeManager _keyframeManager = Substitute.For<IKeyframeManager>();
    private readonly KeyframeSnapper _snapper;
    private readonly Guid _itemId = Guid.NewGuid();

    public KeyframeSnapperTests()
    {
        _snapper = new KeyframeSnapper(
            _keyframeManager,
            NullLogger<KeyframeSnapper>.Instance);
    }

    [Fact]
    public void SnapBefore_ReturnsKeyframeAtOrBeforeTarget()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;
        var keyframeBefore = 28 * TimeSpan.TicksPerSecond;
        var keyframeAfter = 32 * TimeSpan.TicksPerSecond;
        var windowTicks = 5 * TimeSpan.TicksPerSecond;

        SetupKeyframes(keyframeBefore, keyframeAfter);

        var result = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: true, CancellationToken.None);

        Assert.Equal(keyframeBefore, result);
    }

    [Fact]
    public void SnapAfter_ReturnsKeyframeAtOrAfterTarget()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;
        var keyframeBefore = 28 * TimeSpan.TicksPerSecond;
        var keyframeAfter = 32 * TimeSpan.TicksPerSecond;
        var windowTicks = 5 * TimeSpan.TicksPerSecond;

        SetupKeyframes(keyframeBefore, keyframeAfter);

        var result = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: false, CancellationToken.None);

        Assert.Equal(keyframeAfter, result);
    }

    [Fact]
    public void ExactMatch_ReturnsTarget()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;
        var windowTicks = 5 * TimeSpan.TicksPerSecond;

        SetupKeyframes(10 * TimeSpan.TicksPerSecond, targetTicks, 50 * TimeSpan.TicksPerSecond);

        var resultBefore = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: true, CancellationToken.None);
        var resultAfter = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: false, CancellationToken.None);

        Assert.Equal(targetTicks, resultBefore);
        Assert.Equal(targetTicks, resultAfter);
    }

    [Fact]
    public void ReturnsOriginalWhenNoKeyframeInWindow()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;
        var windowTicks = 2 * TimeSpan.TicksPerSecond;

        // Keyframes are far away (>2s)
        SetupKeyframes(10 * TimeSpan.TicksPerSecond, 50 * TimeSpan.TicksPerSecond);

        var result = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: true, CancellationToken.None);

        Assert.Equal(targetTicks, result);
    }

    [Fact]
    public void ReturnsOriginalWhenNoKeyframeData()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;
        var windowTicks = 5 * TimeSpan.TicksPerSecond;

        _keyframeManager.GetKeyframeData(_itemId)
            .Returns(new List<KeyframeData>());

        var result = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: true, CancellationToken.None);

        Assert.Equal(targetTicks, result);
    }

    [Fact]
    public void ReturnsOriginalWhenKeyframeManagerThrows()
    {
        var targetTicks = 30 * TimeSpan.TicksPerSecond;
        var windowTicks = 5 * TimeSpan.TicksPerSecond;

        _keyframeManager.GetKeyframeData(_itemId)
            .Returns(_ => throw new InvalidOperationException("No data"));

        var result = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: true, CancellationToken.None);

        Assert.Equal(targetTicks, result);
    }

    [Fact]
    public void SnapBefore_AtFirstKeyframe_ReturnsIt()
    {
        var targetTicks = 12 * TimeSpan.TicksPerSecond;
        var firstKeyframe = 10 * TimeSpan.TicksPerSecond;
        var windowTicks = 5 * TimeSpan.TicksPerSecond;

        SetupKeyframes(firstKeyframe, 20 * TimeSpan.TicksPerSecond, 30 * TimeSpan.TicksPerSecond);

        var result = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: true, CancellationToken.None);

        Assert.Equal(firstKeyframe, result);
    }

    [Fact]
    public void SnapAfter_AtLastKeyframe_ReturnsIt()
    {
        var lastKeyframe = 50 * TimeSpan.TicksPerSecond;
        var targetTicks = 48 * TimeSpan.TicksPerSecond;
        var windowTicks = 5 * TimeSpan.TicksPerSecond;

        SetupKeyframes(10 * TimeSpan.TicksPerSecond, 30 * TimeSpan.TicksPerSecond, lastKeyframe);

        var result = _snapper.SnapToKeyframe(
            _itemId, targetTicks, windowTicks, snapBefore: false, CancellationToken.None);

        Assert.Equal(lastKeyframe, result);
    }

    [Fact]
    public void CancellationIsRespected()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _snapper.SnapToKeyframe(_itemId, 0, 0, true, cts.Token));
    }

    private void SetupKeyframes(params long[] ticks)
    {
        var data = new KeyframeData(ticks[^1] + TimeSpan.TicksPerSecond, ticks);
        _keyframeManager.GetKeyframeData(_itemId)
            .Returns(new List<KeyframeData> { data });
    }
}
