using System;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Wire DTO mirroring <see cref="Data.Entities.CropDetectResult"/>.
/// </summary>
public class CropDetectResultDto
{
    /// <summary>
    /// Gets or sets the active picture width in pixels.
    /// </summary>
    public int CropWidth { get; set; }

    /// <summary>
    /// Gets or sets the active picture height in pixels.
    /// </summary>
    public int CropHeight { get; set; }

    /// <summary>
    /// Gets or sets the X offset of the active picture from the left edge.
    /// </summary>
    public int CropX { get; set; }

    /// <summary>
    /// Gets or sets the Y offset of the active picture from the top edge.
    /// </summary>
    public int CropY { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when this result was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
