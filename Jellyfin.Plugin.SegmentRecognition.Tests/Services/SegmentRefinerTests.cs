using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

public class SegmentRefinerTests
{
    private readonly FfmpegBlackFrameService _blackFrameService;
    private readonly SegmentRefiner _refiner;

    public SegmentRefinerTests()
    {
        _blackFrameService = Substitute.ForPartsOf<FfmpegBlackFrameService>(
            Substitute.For<IMediaEncoder>(),
            Substitute.For<IConfigurationManager>(),
            NullLogger<FfmpegBlackFrameService>.Instance);

        _refiner = new SegmentRefiner(
            _blackFrameService,
            NullLogger<SegmentRefiner>.Instance);
    }

    [Fact]
    public async Task SnapsStartToNearestSilenceMidpoint()
    {
        var startTicks = 30 * TimeSpan.TicksPerSecond;
        var endTicks = 90 * TimeSpan.TicksPerSecond;

        _blackFrameService.DetectSilenceAsync(
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new List<(double, double)> { (29.5, 30.5) }),
                Task.FromResult(new List<(double, double)> { (89.5, 90.5) }));

        var (refinedStart, refinedEnd) = await _refiner.RefineSegmentAsync(
            startTicks, endTicks, "/fake/path.mkv", null, CancellationToken.None);

        Assert.Equal((long)(30.0 * TimeSpan.TicksPerSecond), refinedStart);
        Assert.Equal((long)(90.0 * TimeSpan.TicksPerSecond), refinedEnd);
    }

    [Fact]
    public async Task SnapsToCloserSilenceGap()
    {
        var startTicks = 30 * TimeSpan.TicksPerSecond;
        var endTicks = 90 * TimeSpan.TicksPerSecond;

        _blackFrameService.DetectSilenceAsync(
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new List<(double, double)> { (30.5, 31.0), (33.0, 33.5) }),
                Task.FromResult(new List<(double, double)> { (89.0, 89.5) }));

        var (refinedStart, refinedEnd) = await _refiner.RefineSegmentAsync(
            startTicks, endTicks, "/fake/path.mkv", null, CancellationToken.None);

        Assert.Equal((long)(30.75 * TimeSpan.TicksPerSecond), refinedStart);
        Assert.Equal((long)(89.25 * TimeSpan.TicksPerSecond), refinedEnd);
    }

    [Fact]
    public async Task ReturnsOriginalWhenNoSilenceFound()
    {
        var startTicks = 30 * TimeSpan.TicksPerSecond;
        var endTicks = 90 * TimeSpan.TicksPerSecond;

        _blackFrameService.DetectSilenceAsync(
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(double, double)>()));

        var (refinedStart, refinedEnd) = await _refiner.RefineSegmentAsync(
            startTicks, endTicks, "/fake/path.mkv", null, CancellationToken.None);

        Assert.Equal(startTicks, refinedStart);
        Assert.Equal(endTicks, refinedEnd);
    }

    [Fact]
    public async Task ReturnsOriginalWhenSilenceDetectionFails()
    {
        var startTicks = 30 * TimeSpan.TicksPerSecond;
        var endTicks = 90 * TimeSpan.TicksPerSecond;

        _blackFrameService.DetectSilenceAsync(
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<(double, double)>>>(
                _ => throw new InvalidOperationException("ffmpeg failed"));

        var (refinedStart, refinedEnd) = await _refiner.RefineSegmentAsync(
            startTicks, endTicks, "/fake/path.mkv", null, CancellationToken.None);

        Assert.Equal(startTicks, refinedStart);
        Assert.Equal(endTicks, refinedEnd);
    }

    [Fact]
    public async Task ReturnsOriginalWhenRefinementWouldInvertRange()
    {
        var startTicks = 30 * TimeSpan.TicksPerSecond;
        var endTicks = 32 * TimeSpan.TicksPerSecond;

        _blackFrameService.DetectSilenceAsync(
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(),
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new List<(double, double)> { (33.5, 34.5) }),
                Task.FromResult(new List<(double, double)> { (28.5, 29.5) }));

        var (refinedStart, refinedEnd) = await _refiner.RefineSegmentAsync(
            startTicks, endTicks, "/fake/path.mkv", null, CancellationToken.None);

        Assert.Equal(startTicks, refinedStart);
        Assert.Equal(endTicks, refinedEnd);
    }
}
