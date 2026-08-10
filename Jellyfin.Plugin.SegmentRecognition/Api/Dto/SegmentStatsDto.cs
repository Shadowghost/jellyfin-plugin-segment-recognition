using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Aggregate statistics returned by <c>GET /SegmentRecognition/Stats</c>.
/// </summary>
public class SegmentStatsDto
{
    /// <summary>
    /// Gets or sets the total number of items that have been analyzed by at least one provider.
    /// </summary>
    public int TotalAnalyzedItems { get; set; }

    /// <summary>
    /// Gets or sets the per-provider counters.
    /// </summary>
    public IReadOnlyList<ProviderStatsDto> PerProvider { get; set; } = [];
}
