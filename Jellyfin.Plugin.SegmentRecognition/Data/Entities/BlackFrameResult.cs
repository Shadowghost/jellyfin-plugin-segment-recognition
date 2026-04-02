using System;

namespace Jellyfin.Plugin.SegmentRecognition.Data.Entities;

/// <summary>
/// Stores a detected black frame from ffmpeg analysis.
/// Compound key: (ItemId, TimestampTicks).
/// </summary>
public class BlackFrameResult
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp in ticks.
    /// </summary>
    public long TimestampTicks { get; set; }

    /// <summary>
    /// Gets or sets the percentage of black pixels.
    /// </summary>
    public double BlackPercentage { get; set; }

    /// <summary>
    /// Gets or sets a hash of the configuration parameters that were active when this
    /// result was created. Used to detect stale results after config changes.
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp when this result was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
