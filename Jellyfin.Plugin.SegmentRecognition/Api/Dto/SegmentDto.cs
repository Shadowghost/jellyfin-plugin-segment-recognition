namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Lightweight segment wire type shared by <see cref="ProviderSegmentsDto"/>.
/// Timestamps are milliseconds (sub-ms precision preserved as a double).
/// </summary>
public class SegmentDto
{
    /// <summary>
    /// Gets or sets the segment type as the
    /// <see cref="Database.Implementations.Enums.MediaSegmentType"/> enum name.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the segment start position in milliseconds.
    /// </summary>
    public double StartMs { get; set; }

    /// <summary>
    /// Gets or sets the segment end position in milliseconds.
    /// </summary>
    public double EndMs { get; set; }
}
