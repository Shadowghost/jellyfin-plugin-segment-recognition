using System;

namespace Jellyfin.Plugin.SegmentRecognition.Data.Entities;

/// <summary>
/// Stores a chromaprint audio fingerprint for an item.
/// Compound key: (ItemId, Region).
/// </summary>
public class ChromaprintResult
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the region this fingerprint covers (e.g. "Intro" or "Credits").
    /// </summary>
    public required string Region { get; set; }

    /// <summary>
    /// Gets or sets the season identifier.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the raw fingerprint data.
    /// </summary>
#pragma warning disable CA1819 // Properties should not return arrays - byte[] needed for EF Core blob storage
    public required byte[] FingerprintData { get; set; }
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets the duration in seconds that was analyzed.
    /// </summary>
    public int AnalysisDurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets a hash of the configuration parameters that were active when this
    /// fingerprint was generated. Used to detect stale results after config changes.
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp when this result was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
