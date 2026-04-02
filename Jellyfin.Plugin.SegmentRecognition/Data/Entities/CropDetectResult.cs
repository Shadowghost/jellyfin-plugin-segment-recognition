using System;

namespace Jellyfin.Plugin.SegmentRecognition.Data.Entities;

/// <summary>
/// Stores the detected active video area (excluding letterbox/pillarbox bars) for an item.
/// Primary key: ItemId (one crop result per item).
/// </summary>
public class CropDetectResult
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the width of the detected active area in pixels.
    /// </summary>
    public int CropWidth { get; set; }

    /// <summary>
    /// Gets or sets the height of the detected active area in pixels.
    /// </summary>
    public int CropHeight { get; set; }

    /// <summary>
    /// Gets or sets the X offset of the active area from the left edge.
    /// </summary>
    public int CropX { get; set; }

    /// <summary>
    /// Gets or sets the Y offset of the active area from the top edge.
    /// </summary>
    public int CropY { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when this result was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
