using System;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Per-provider counters used by <see cref="SegmentStatsDto"/>.
/// </summary>
public class ProviderStatsDto
{
    /// <summary>
    /// Gets or sets the provider name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of items the provider has analyzed.
    /// </summary>
    public int AnalyzedItems { get; set; }

    /// <summary>
    /// Gets or sets the number of items for which the provider produced at least one result.
    /// </summary>
    public int ItemsWithResults { get; set; }

    /// <summary>
    /// Gets or sets the most recent analysis timestamp for this provider.
    /// </summary>
    public DateTime? LastAnalyzedAt { get; set; }
}
