using System;
using System.IO;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

public sealed class EdlParsingTests : IDisposable
{
    private readonly EdlImportProvider _provider;
    private readonly string _tempDir;
    private readonly Guid _itemId = Guid.NewGuid();

    public EdlParsingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "edl-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _provider = new EdlImportProvider(
            Substitute.For<ILibraryManager>(),
            Substitute.For<IDbContextFactory<Data.SegmentDbContext>>(),
            NullLogger<EdlImportProvider>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void ExtendedFormat_ParsesExplicitTypeNames()
    {
        var edlPath = WriteEdl(
            "0.00\t90.00\t0\tIntro",
            "1200.00\t1350.00\t0\tOutro");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Equal(2, segments.Count);
        Assert.Equal(MediaSegmentType.Intro, segments[0].Type);
        Assert.Equal(0L, segments[0].StartTicks);
        Assert.Equal(90 * TimeSpan.TicksPerSecond, segments[0].EndTicks);
        Assert.Equal(MediaSegmentType.Outro, segments[1].Type);
    }

    [Fact]
    public void ExtendedFormat_AllTypeNames()
    {
        var edlPath = WriteEdl(
            "0.00\t5.00\t0\tRecap",
            "5.00\t90.00\t0\tIntro",
            "1200.00\t1350.00\t0\tOutro",
            "1350.00\t1400.00\t0\tPreview",
            "600.00\t660.00\t3");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Equal(5, segments.Count);
        Assert.Equal(MediaSegmentType.Recap, segments[0].Type);
        Assert.Equal(MediaSegmentType.Intro, segments[1].Type);
        Assert.Equal(MediaSegmentType.Outro, segments[2].Type);
        Assert.Equal(MediaSegmentType.Preview, segments[3].Type);
        Assert.Equal(MediaSegmentType.Commercial, segments[4].Type);
    }

    [Fact]
    public void StandardFormat_SingleFirstHalf_ClassifiesAsIntro()
    {
        // Single action-0 segment in first half of a 1500s file
        var edlPath = WriteEdl("5.00\t90.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Single(segments);
        Assert.Equal(MediaSegmentType.Intro, segments[0].Type);
    }

    [Fact]
    public void StandardFormat_SingleSecondHalf_ClassifiesAsOutro()
    {
        // Single action-0 segment in second half of a 1500s file
        var edlPath = WriteEdl("1200.00\t1350.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Single(segments);
        Assert.Equal(MediaSegmentType.Outro, segments[0].Type);
    }

    [Fact]
    public void StandardFormat_TwoFirstHalf_RecapThenIntro()
    {
        // Two action-0 segments in first half → earliest=Recap, next=Intro
        var edlPath = WriteEdl(
            "0.00\t5.00\t0",
            "5.00\t90.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Equal(2, segments.Count);
        Assert.Equal(MediaSegmentType.Recap, segments[0].Type);
        Assert.Equal(MediaSegmentType.Intro, segments[1].Type);
    }

    [Fact]
    public void StandardFormat_TwoSecondHalf_OutroThenPreview()
    {
        // Two action-0 segments in second half → earliest=Outro, next=Preview
        var edlPath = WriteEdl(
            "1200.00\t1300.00\t0",
            "1300.00\t1400.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Equal(2, segments.Count);
        Assert.Equal(MediaSegmentType.Outro, segments[0].Type);
        Assert.Equal(MediaSegmentType.Preview, segments[1].Type);
    }

    [Fact]
    public void StandardFormat_FullTvPattern()
    {
        // Full TV pattern: [Recap] [Intro] ... [Outro] [Preview]
        var edlPath = WriteEdl(
            "0.00\t5.00\t0",
            "5.00\t90.00\t0",
            "1200.00\t1350.00\t0",
            "1350.00\t1400.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Equal(4, segments.Count);
        Assert.Equal(MediaSegmentType.Recap, segments[0].Type);
        Assert.Equal(MediaSegmentType.Intro, segments[1].Type);
        Assert.Equal(MediaSegmentType.Outro, segments[2].Type);
        Assert.Equal(MediaSegmentType.Preview, segments[3].Type);
    }

    [Fact]
    public void Action3_MapsToCommercial()
    {
        var edlPath = WriteEdl("300.00\t360.00\t3");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Single(segments);
        Assert.Equal(MediaSegmentType.Commercial, segments[0].Type);
    }

    [Fact]
    public void IgnoresAction1And2()
    {
        var edlPath = WriteEdl(
            "0.00\t5.00\t1",
            "5.00\t10.00\t2");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Empty(segments);
    }

    [Fact]
    public void SkipsInvalidLines()
    {
        var edlPath = WriteEdl(
            "# comment line",
            "",
            "not a number\t5.00\t0",
            "5.00\t90.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Single(segments);
        Assert.Equal(MediaSegmentType.Intro, segments[0].Type);
    }

    [Fact]
    public void SkipsEndBeforeOrEqualStart()
    {
        var edlPath = WriteEdl(
            "90.00\t90.00\t0",
            "100.00\t50.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Empty(segments);
    }

    [Fact]
    public void SpaceDelimitedEdlFile()
    {
        var edlPath = WriteEdl("5.00 90.00 0 Intro");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Single(segments);
        Assert.Equal(MediaSegmentType.Intro, segments[0].Type);
    }

    [Fact]
    public void ZeroRuntime_AllSegmentsClassifiedAsFirstHalf()
    {
        // Unknown runtime — all segments treated as first-half
        var edlPath = WriteEdl(
            "0.00\t5.00\t0",
            "1200.00\t1350.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 0);

        Assert.Equal(2, segments.Count);
        // Both in "first half" — first becomes Recap (2+ segments), second becomes Intro
        Assert.Equal(MediaSegmentType.Recap, segments[0].Type);
        Assert.Equal(MediaSegmentType.Intro, segments[1].Type);
    }

    [Fact]
    public void TypeNameIsCaseInsensitive()
    {
        var edlPath = WriteEdl("5.00\t90.00\t0\tiNtRo");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Single(segments);
        Assert.Equal(MediaSegmentType.Intro, segments[0].Type);
    }

    [Fact]
    public void MixedTypedAndUntyped()
    {
        // First segment has explicit type, second doesn't
        var edlPath = WriteEdl(
            "0.00\t5.00\t0\tRecap",
            "5.00\t90.00\t0");

        var segments = _provider.ParseEdlFile(edlPath, _itemId, runtimeTicks: 1500 * TimeSpan.TicksPerSecond);

        Assert.Equal(2, segments.Count);
        Assert.Equal(MediaSegmentType.Recap, segments[0].Type);
        // The untyped segment at 5-90s is in the first half, and it's the only untyped one → Intro
        Assert.Equal(MediaSegmentType.Intro, segments[1].Type);
    }

    private string WriteEdl(params string[] lines)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".edl");
        File.WriteAllLines(path, lines);
        return path;
    }
}
