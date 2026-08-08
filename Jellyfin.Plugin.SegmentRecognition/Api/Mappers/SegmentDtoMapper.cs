using System;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Api.Dto;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Mappers;

/// <summary>
/// Maps EF Core entities to wire DTOs. Conversions:
/// <list type="bullet">
/// <item>ticks (1 tick = 100 ns) become double-precision milliseconds via <see cref="TicksToMilliseconds"/>.</item>
/// <item>integer <see cref="MediaSegmentType"/> values become their enum name (or <c>"Unknown"</c> for out-of-range values).</item>
/// <item>raw chromaprint bytes are never copied to the wire format; only the byte length is.</item>
/// <item>timestamps are stamped <see cref="DateTimeKind.Utc"/> via <see cref="AsUtc(DateTime)"/>.</item>
/// </list>
/// </summary>
public static class SegmentDtoMapper
{
    private const double TicksPerMillisecond = TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// Converts a tick count to milliseconds with sub-ms precision.
    /// </summary>
    /// <param name="ticks">The tick value.</param>
    /// <returns>Milliseconds as a double.</returns>
    public static double TicksToMilliseconds(long ticks) => ticks / TicksPerMillisecond;

    /// <summary>
    /// Stamps a timestamp read back from the database as UTC.
    /// </summary>
    /// <remarks>
    /// Every write site stores <see cref="DateTime.UtcNow"/>, but SQLite has no notion of an
    /// offset: EF reads the TEXT column back as <see cref="DateTimeKind.Unspecified"/>, which
    /// System.Text.Json then serializes without a <c>Z</c> suffix. Browsers parse that as local
    /// time, so every timestamp in the API would be silently shifted by the viewer's offset.
    /// </remarks>
    /// <param name="value">The value read from the database.</param>
    /// <returns>The same instant, tagged as UTC.</returns>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Nullable overload of <see cref="AsUtc(DateTime)"/>.
    /// </summary>
    /// <param name="value">The value read from the database, or <c>null</c>.</param>
    /// <returns>The same instant tagged as UTC, or <c>null</c>.</returns>
    public static DateTime? AsUtc(DateTime? value) => value is null ? null : AsUtc(value.Value);

    /// <summary>
    /// Returns the <see cref="MediaSegmentType"/> name for the given integer, falling back to
    /// <c>"Unknown"</c> when the value is not a defined enum member.
    /// </summary>
    /// <param name="segmentType">The integer segment type value.</param>
    /// <returns>The enum name or "Unknown".</returns>
    public static string SegmentTypeName(int segmentType)
    {
        var typed = (MediaSegmentType)segmentType;
        return Enum.IsDefined(typed) ? typed.ToString() : nameof(MediaSegmentType.Unknown);
    }

    /// <summary>
    /// Maps an <see cref="AnalysisStatus"/> entity to its DTO.
    /// </summary>
    /// <param name="entity">The entity to map.</param>
    /// <returns>The DTO.</returns>
    public static AnalysisStatusDto ToDto(AnalysisStatus entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new AnalysisStatusDto
        {
            ProviderName = entity.ProviderName,
            AnalyzedAt = AsUtc(entity.AnalyzedAt),
            HasResults = entity.HasResults,
            LastError = entity.LastError,
            LastErrorAt = AsUtc(entity.LastErrorAt),
            IntroOutcome = OutcomeName(entity.IntroOutcome),
            OutroOutcome = OutcomeName(entity.OutroOutcome)
        };
    }

    /// <summary>
    /// Renders a <see cref="SegmentMatchOutcome"/> as its enum name, matching how
    /// <see cref="SegmentTypeName"/> exposes segment types on the wire.
    /// </summary>
    /// <param name="outcome">The stored outcome, or <c>null</c>.</param>
    /// <returns>The enum name, or <c>null</c> when unset or not a defined member.</returns>
    public static string? OutcomeName(SegmentMatchOutcome? outcome)
    {
        return outcome is { } value && Enum.IsDefined(value) ? value.ToString() : null;
    }

    /// <summary>
    /// Maps a <see cref="ChapterAnalysisResult"/> entity to its DTO.
    /// </summary>
    /// <param name="entity">The entity to map.</param>
    /// <returns>The DTO.</returns>
    public static ChapterAnalysisResultDto ToDto(ChapterAnalysisResult entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new ChapterAnalysisResultDto
        {
            SegmentType = SegmentTypeName(entity.SegmentType),
            StartMs = TicksToMilliseconds(entity.StartTicks),
            EndMs = TicksToMilliseconds(entity.EndTicks),
            MatchedChapterName = entity.MatchedChapterName,
            ConfigHash = entity.ConfigHash,
            CreatedAt = AsUtc(entity.CreatedAt)
        };
    }

    /// <summary>
    /// Maps a <see cref="BlackFrameResult"/> entity to its DTO.
    /// </summary>
    /// <param name="entity">The entity to map.</param>
    /// <returns>The DTO.</returns>
    public static BlackFrameResultDto ToDto(BlackFrameResult entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new BlackFrameResultDto
        {
            TimestampMs = TicksToMilliseconds(entity.TimestampTicks),
            BlackPercentage = entity.BlackPercentage,
            CreatedAt = AsUtc(entity.CreatedAt)
        };
    }

    /// <summary>
    /// Maps a <see cref="ChromaprintResult"/> entity to its DTO. The raw fingerprint bytes are
    /// not included; only the length is exposed.
    /// </summary>
    /// <param name="entity">The entity to map.</param>
    /// <returns>The DTO.</returns>
    public static ChromaprintResultDto ToDto(ChromaprintResult entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new ChromaprintResultDto
        {
            Region = entity.Region,
            SeasonId = entity.SeasonId,
            AnalysisDurationSeconds = entity.AnalysisDurationSeconds,
            FingerprintLength = entity.FingerprintData?.Length ?? 0,
            CreatedAt = AsUtc(entity.CreatedAt)
        };
    }

    /// <summary>
    /// Maps a <see cref="CropDetectResult"/> entity to its DTO.
    /// </summary>
    /// <param name="entity">The entity to map.</param>
    /// <returns>The DTO.</returns>
    public static CropDetectResultDto ToDto(CropDetectResult entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new CropDetectResultDto
        {
            CropWidth = entity.CropWidth,
            CropHeight = entity.CropHeight,
            CropX = entity.CropX,
            CropY = entity.CropY,
            CreatedAt = AsUtc(entity.CreatedAt)
        };
    }
}
