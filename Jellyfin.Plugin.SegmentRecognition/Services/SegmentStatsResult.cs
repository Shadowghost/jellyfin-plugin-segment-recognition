using System;
using Jellyfin.Plugin.SegmentRecognition.Api.Dto;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Result of <see cref="SegmentDataQueryService.GetStatsAsync"/>: the DTO plus the inputs the
/// controller needs to compute a conditional-GET ETag.
/// </summary>
/// <param name="Dto">The aggregate statistics.</param>
/// <param name="Watermark">The most recent analysis timestamp across all items.</param>
/// <param name="RowCount">The number of <see cref="Data.Entities.AnalysisStatus"/> rows considered.</param>
public sealed record SegmentStatsResult(SegmentStatsDto Dto, DateTime? Watermark, int RowCount);
