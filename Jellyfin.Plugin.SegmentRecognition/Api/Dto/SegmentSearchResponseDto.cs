using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Paginated response for <c>GET /SegmentRecognition/Segments</c>.
/// </summary>
public class SegmentSearchResponseDto
{
    /// <summary>
    /// Gets or sets the rows in the requested window.
    /// </summary>
    public IReadOnlyList<SegmentSearchResultDto> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the total row count matching the filter (before <c>limit</c>/<c>startIndex</c>).
    /// </summary>
    public int TotalRecordCount { get; set; }
}
