using System;
using Jellyfin.Plugin.SegmentRecognition.Api.Dto;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Result of <see cref="SegmentDataQueryService.GetItemDataAsync"/>: the DTO plus the inputs the
/// controller needs to compute a conditional-GET ETag.
/// </summary>
/// <param name="Dto">The aggregated per-provider data.</param>
/// <param name="Watermark">
/// The most recent mutation timestamp across the item's providers: the newest of
/// <see cref="Data.Entities.AnalysisStatus.AnalyzedAt"/> and
/// <see cref="Data.Entities.AnalysisStatus.LastErrorAt"/>. Recording or clearing a provider error
/// does not touch <c>AnalyzedAt</c>, so an analysis-only watermark would let a cached client sit
/// on a <c>304</c> and never observe a newly captured failure.
/// </param>
/// <param name="RowCount">The number of rows behind the DTO, across every table it draws from.</param>
/// <param name="HasAnyData">Whether the plugin holds any row at all for this item.</param>
public sealed record ItemSegmentDataResult(ItemSegmentDataDto Dto, DateTime? Watermark, int RowCount, bool HasAnyData);
