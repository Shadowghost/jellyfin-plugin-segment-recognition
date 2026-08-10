using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Segments returned by a single plugin provider when asked to reshape its stored
/// analysis data for an item.
/// </summary>
public class ProviderSegmentsDto
{
    /// <summary>
    /// Gets or sets the provider name (matches <see cref="AnalysisStatusDto.ProviderName"/>).
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this provider claims to support the item.
    /// When false, the provider was not queried and <see cref="Segments"/> is empty.
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// Gets or sets the segments the provider would hand Jellyfin for this item.
    /// </summary>
    public IReadOnlyList<SegmentDto> Segments { get; set; } = [];

    /// <summary>
    /// Gets or sets an error message if the provider threw while producing segments.
    /// </summary>
    public string? Error { get; set; }
}
