using System;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Api.Dto;
using Jellyfin.Plugin.SegmentRecognition.Api.Mappers;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Api;

public sealed class SegmentDtoMapperTests
{
    [Fact]
    public void TicksToMilliseconds_Zero_ReturnsZero()
    {
        Assert.Equal(0, SegmentDtoMapper.TicksToMilliseconds(0));
    }

    [Fact]
    public void TicksToMilliseconds_OneSecond_ReturnsThousand()
    {
        // 1 second = 10_000_000 ticks (TimeSpan.TicksPerSecond)
        Assert.Equal(1000d, SegmentDtoMapper.TicksToMilliseconds(TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void TicksToMilliseconds_SubMillisecond_PreservesPrecision()
    {
        // 5_000 ticks = 0.5 ms
        Assert.Equal(0.5d, SegmentDtoMapper.TicksToMilliseconds(5_000));
    }

    [Theory]
    [InlineData(0, "Unknown")]
    [InlineData(1, "Commercial")]
    [InlineData(2, "Preview")]
    [InlineData(3, "Recap")]
    [InlineData(4, "Outro")]
    [InlineData(5, "Intro")]
    public void SegmentTypeName_KnownValues_ReturnsEnumName(int value, string expected)
    {
        Assert.Equal(expected, SegmentDtoMapper.SegmentTypeName(value));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void SegmentTypeName_UnknownValues_FallsBackToUnknown(int value)
    {
        Assert.Equal(nameof(MediaSegmentType.Unknown), SegmentDtoMapper.SegmentTypeName(value));
    }

    [Fact]
    public void ChapterAnalysisResult_RoundTrip_ConvertsTicksAndEnum()
    {
        var entity = new ChapterAnalysisResult
        {
            ItemId = Guid.NewGuid(),
            SegmentType = (int)MediaSegmentType.Intro,
            StartTicks = TimeSpan.FromSeconds(10).Ticks,
            EndTicks = TimeSpan.FromSeconds(45).Ticks,
            MatchedChapterName = "Opening",
            ConfigHash = "abc",
            CreatedAt = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        var dto = SegmentDtoMapper.ToDto(entity);

        Assert.Equal("Intro", dto.SegmentType);
        Assert.Equal(10_000d, dto.StartMs);
        Assert.Equal(45_000d, dto.EndMs);
        Assert.Equal("Opening", dto.MatchedChapterName);
        Assert.Equal("abc", dto.ConfigHash);
        Assert.Equal(entity.CreatedAt, dto.CreatedAt);
    }

    [Fact]
    public void BlackFrameResult_RoundTrip_ConvertsTicks()
    {
        var entity = new BlackFrameResult
        {
            ItemId = Guid.NewGuid(),
            TimestampTicks = TimeSpan.FromSeconds(7.5).Ticks,
            BlackPercentage = 99.2,
            CreatedAt = DateTime.UtcNow
        };

        var dto = SegmentDtoMapper.ToDto(entity);

        Assert.Equal(7_500d, dto.TimestampMs);
        Assert.Equal(99.2, dto.BlackPercentage);
    }

    [Fact]
    public void ChromaprintResult_DoesNotLeakFingerprintBytes()
    {
        var bytes = new byte[1234];
        var entity = new ChromaprintResult
        {
            ItemId = Guid.NewGuid(),
            Region = "Intro",
            SeasonId = Guid.NewGuid(),
            FingerprintData = bytes,
            AnalysisDurationSeconds = 60,
            CreatedAt = DateTime.UtcNow
        };

        var dto = SegmentDtoMapper.ToDto(entity);

        // The DTO type intentionally has no FingerprintData property; only length is exposed.
        var props = typeof(ChromaprintResultDto).GetProperties();
        Assert.DoesNotContain(props, p => p.PropertyType == typeof(byte[]));
        Assert.Equal(1234, dto.FingerprintLength);
        Assert.Equal("Intro", dto.Region);
        Assert.Equal(60, dto.AnalysisDurationSeconds);
    }

    [Fact]
    public void ChromaprintResult_NullFingerprint_ReturnsZeroLength()
    {
        var entity = new ChromaprintResult
        {
            ItemId = Guid.NewGuid(),
            Region = "Credits",
            SeasonId = Guid.NewGuid(),
            FingerprintData = null!,
            AnalysisDurationSeconds = 30,
            CreatedAt = DateTime.UtcNow
        };

        var dto = SegmentDtoMapper.ToDto(entity);

        Assert.Equal(0, dto.FingerprintLength);
    }

    [Fact]
    public void AnalysisStatus_RoundTrip()
    {
        var entity = new AnalysisStatus
        {
            ItemId = Guid.NewGuid(),
            ProviderName = "BlackFrame",
            AnalyzedAt = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc),
            HasResults = true
        };

        var dto = SegmentDtoMapper.ToDto(entity);

        Assert.Equal("BlackFrame", dto.ProviderName);
        Assert.Equal(entity.AnalyzedAt, dto.AnalyzedAt);
        Assert.True(dto.HasResults);
        Assert.Null(dto.LastError);
        Assert.Null(dto.LastErrorAt);
    }

    [Fact]
    public void AnalysisStatus_LastError_PropagatesToDto()
    {
        var when = new DateTime(2026, 5, 1, 9, 30, 0, DateTimeKind.Utc);
        var entity = new AnalysisStatus
        {
            ItemId = Guid.NewGuid(),
            ProviderName = "Chromaprint",
            AnalyzedAt = when,
            HasResults = false,
            LastError = "InvalidOperationException: ffmpeg exited with code 1",
            LastErrorAt = when,
        };

        var dto = SegmentDtoMapper.ToDto(entity);

        Assert.Equal(entity.LastError, dto.LastError);
        Assert.Equal(entity.LastErrorAt, dto.LastErrorAt);
    }

    [Fact]
    public void CropDetectResult_RoundTrip()
    {
        var entity = new CropDetectResult
        {
            ItemId = Guid.NewGuid(),
            CropWidth = 1920,
            CropHeight = 800,
            CropX = 0,
            CropY = 140,
            CreatedAt = DateTime.UtcNow
        };

        var dto = SegmentDtoMapper.ToDto(entity);

        Assert.Equal(1920, dto.CropWidth);
        Assert.Equal(800, dto.CropHeight);
        Assert.Equal(0, dto.CropX);
        Assert.Equal(140, dto.CropY);
    }
}
