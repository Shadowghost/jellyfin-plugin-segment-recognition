using System;
using Jellyfin.Plugin.SegmentRecognition.Api.Mappers;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Api;

/// <summary>
/// Tests that every timestamp leaving the API is tagged UTC.
/// </summary>
/// <remarks>
/// Writers store <see cref="DateTime.UtcNow"/>, but SQLite has no offset: EF reads the TEXT column
/// back as <see cref="DateTimeKind.Unspecified"/>, which System.Text.Json then serializes without a
/// <c>Z</c>. A browser parses that as local time, so the whole API was off by the viewer's offset.
/// </remarks>
public class TimestampKindTests
{
    [Fact]
    public void AsUtc_TagsAnUnspecifiedValueWithoutShiftingIt()
    {
        var stored = new DateTime(2026, 8, 8, 12, 30, 0, DateTimeKind.Unspecified);

        var mapped = SegmentDtoMapper.AsUtc(stored);

        Assert.Equal(DateTimeKind.Utc, mapped.Kind);
        Assert.Equal(stored.Ticks, mapped.Ticks);
    }

    [Fact]
    public void AsUtc_LeavesAUtcValueAlone()
    {
        var utc = new DateTime(2026, 8, 8, 12, 30, 0, DateTimeKind.Utc);

        Assert.Equal(utc, SegmentDtoMapper.AsUtc(utc));
        Assert.Equal(DateTimeKind.Utc, SegmentDtoMapper.AsUtc(utc).Kind);
    }

    [Fact]
    public void AsUtc_ConvertsALocalValue()
    {
        var local = new DateTime(2026, 8, 8, 12, 30, 0, DateTimeKind.Local);

        var mapped = SegmentDtoMapper.AsUtc(local);

        Assert.Equal(DateTimeKind.Utc, mapped.Kind);
        Assert.Equal(local.ToUniversalTime(), mapped);
    }

    [Fact]
    public void AsUtc_PassesNullThrough()
    {
        Assert.Null(SegmentDtoMapper.AsUtc((DateTime?)null));
    }

    [Fact]
    public void MappedStatus_CarriesUtcOnBothTimestamps()
    {
        var status = new AnalysisStatus
        {
            ItemId = Guid.NewGuid(),
            ProviderName = "ChapterName",
            AnalyzedAt = new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Unspecified),
            LastErrorAt = new DateTime(2026, 8, 8, 11, 0, 0, DateTimeKind.Unspecified),
            HasResults = true,
        };

        var dto = SegmentDtoMapper.ToDto(status);

        Assert.Equal(DateTimeKind.Utc, dto.AnalyzedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, dto.LastErrorAt!.Value.Kind);
    }

    [Fact]
    public void MappedResults_CarryUtcCreatedAt()
    {
        var stored = new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Unspecified);

        var chapter = SegmentDtoMapper.ToDto(new ChapterAnalysisResult
        {
            ItemId = Guid.NewGuid(),
            MatchedChapterName = "Intro",
            CreatedAt = stored,
        });
        var blackFrame = SegmentDtoMapper.ToDto(new BlackFrameResult
        {
            ItemId = Guid.NewGuid(),
            ConfigHash = "h",
            CreatedAt = stored,
        });
        var crop = SegmentDtoMapper.ToDto(new CropDetectResult
        {
            ItemId = Guid.NewGuid(),
            CreatedAt = stored,
        });

        Assert.Equal(DateTimeKind.Utc, chapter.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, blackFrame.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, crop.CreatedAt.Kind);
    }
}
