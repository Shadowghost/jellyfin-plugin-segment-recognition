using System;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Per-row entry in <see cref="SegmentSearchResponseDto"/>.
/// </summary>
public class SegmentSearchResultDto
{
    /// <summary>
    /// Gets or sets the Jellyfin item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the matched chapter name (the source label for cached chapter/chromaprint rows).
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the segment type as the
    /// <see cref="Database.Implementations.Enums.MediaSegmentType"/> enum name.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the segment start in milliseconds.
    /// </summary>
    public double StartMs { get; set; }

    /// <summary>
    /// Gets or sets the segment end in milliseconds.
    /// </summary>
    public double EndMs { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when this row was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
