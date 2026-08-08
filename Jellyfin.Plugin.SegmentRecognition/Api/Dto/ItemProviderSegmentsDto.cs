using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Aggregated per-provider segment response returned by
/// <c>GET /SegmentRecognition/Items/{itemId}/ProviderSegments</c>.
/// </summary>
public class ItemProviderSegmentsDto
{
    /// <summary>
    /// Gets or sets the Jellyfin item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the per-provider segment lists, in provider-registration order.
    /// </summary>
    public IReadOnlyList<ProviderSegmentsDto> Providers { get; set; } = [];
}
