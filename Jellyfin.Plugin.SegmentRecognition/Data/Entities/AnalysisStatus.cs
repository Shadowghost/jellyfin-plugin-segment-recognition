using System;

namespace Jellyfin.Plugin.SegmentRecognition.Data.Entities;

/// <summary>
/// Tracks whether an item was analyzed by a given provider.
/// Compound key: (ItemId, ProviderName).
/// </summary>
public class AnalysisStatus
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the provider name (e.g. "BlackFrame", "Chromaprint", "ChapterName").
    /// </summary>
    public required string ProviderName { get; set; }

    /// <summary>
    /// Gets or sets the date/time the analysis was performed.
    /// </summary>
    public DateTime AnalyzedAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the analysis found any results.
    /// </summary>
    public bool HasResults { get; set; }

    /// <summary>
    /// Gets or sets the provider-specific config hash that was in effect when this status
    /// row was written. Used to detect staleness for groups that previously produced zero
    /// results: those rows have no entries in result tables, so the existing hash-on-result
    /// check can't see them. For Chromaprint this stores the comparison-config hash; for
    /// other providers it's the provider's own config hash. Nullable so legacy rows written
    /// before this column existed are still valid (treated as always stale on first read).
    /// </summary>
    public string? ConfigHash { get; set; }
}
