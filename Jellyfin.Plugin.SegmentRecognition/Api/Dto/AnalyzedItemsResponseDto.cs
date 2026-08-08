using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Paginated response for <c>GET /SegmentRecognition/Items</c>.
/// </summary>
public class AnalyzedItemsResponseDto
{
    /// <summary>
    /// Gets or sets the items in the requested window.
    /// </summary>
    public IReadOnlyList<AnalyzedItemSummaryDto> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the total number of analyzed items under the requested parent.
    /// </summary>
    public int TotalRecordCount { get; set; }
}
