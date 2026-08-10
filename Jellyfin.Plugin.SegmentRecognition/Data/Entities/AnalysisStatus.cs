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
    /// Gets or sets the container (rollup) identifier this item belongs to: the series id for
    /// episodes, otherwise the item's own id. Stored so the analyzed-items listing can roll up
    /// to the container level in SQL instead of hydrating every leaf item from Jellyfin.
    /// <see cref="Guid.Empty"/> marks a legacy row that predates this column and is awaiting
    /// backfill; such rows are excluded from the listing until resolved.
    /// </summary>
    public Guid ContainerId { get; set; }

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

    /// <summary>
    /// Gets or sets the last error captured for this provider/item, if any. Populated when
    /// a manual recalculate or a scheduled run wraps a provider invocation that throws.
    /// Cleared on a clean run. Nullable so a "no error" state is unambiguous.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp <see cref="LastError"/> was last updated.
    /// </summary>
    public DateTime? LastErrorAt { get; set; }

    /// <summary>
    /// Gets or sets why the last run did or did not produce an intro for this item.
    /// <c>null</c> when the provider does not look for intros, or on a legacy row written before
    /// this column existed. Distinct from <see cref="LastError"/>, which means the provider
    /// failed: a recorded outcome is a normal, successful run that simply had nothing to match.
    /// </summary>
    public SegmentMatchOutcome? IntroOutcome { get; set; }

    /// <summary>
    /// Gets or sets why the last run did or did not produce an outro/credits segment for this
    /// item. See <see cref="IntroOutcome"/>.
    /// </summary>
    public SegmentMatchOutcome? OutroOutcome { get; set; }
}
