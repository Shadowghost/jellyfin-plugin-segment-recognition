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
}
