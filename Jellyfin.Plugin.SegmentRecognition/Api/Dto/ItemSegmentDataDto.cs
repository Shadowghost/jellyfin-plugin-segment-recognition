using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Aggregated per-item analysis data returned by
/// <c>GET /SegmentRecognition/v1/Items/{itemId}</c>.
/// </summary>
public class ItemSegmentDataDto
{
    /// <summary>
    /// Gets or sets the Jellyfin item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the analysis statuses, one per provider that has touched this item.
    /// </summary>
    public IReadOnlyList<AnalysisStatusDto> AnalysisStatuses { get; set; } = [];

    /// <summary>
    /// Gets or sets the chapter-name analysis results for this item.
    /// </summary>
    public IReadOnlyList<ChapterAnalysisResultDto> ChapterResults { get; set; } = [];

    /// <summary>
    /// Gets or sets the detected black-frame samples for this item (capped to limit response size).
    /// </summary>
    public IReadOnlyList<BlackFrameResultDto> BlackFrames { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="BlackFrames"/> was truncated by the
    /// per-item row cap. When <c>true</c> the list is a prefix ordered by timestamp, not the
    /// complete set of samples.
    /// </summary>
    public bool BlackFramesTruncated { get; set; }

    /// <summary>
    /// Gets or sets the chromaprint fingerprint metadata for this item.
    /// </summary>
    public IReadOnlyList<ChromaprintResultDto> ChromaprintResults { get; set; } = [];

    /// <summary>
    /// Gets or sets the crop-detect result for this item, if any. Populated as a byproduct of
    /// black-frame analysis (the crop rectangle is measured on the same ffmpeg pass), so it is
    /// present only for items the black-frame provider has processed and is null otherwise.
    /// </summary>
    public CropDetectResultDto? CropDetect { get; set; }
}
