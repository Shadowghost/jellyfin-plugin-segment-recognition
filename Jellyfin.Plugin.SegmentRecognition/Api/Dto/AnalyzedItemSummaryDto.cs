using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Lightweight per-item record returned by
/// <c>GET /SegmentRecognition/v1/Items?parentId=...</c>. Includes enough metadata for the
/// poster grid to render without additional Jellyfin API calls.
/// </summary>
public class AnalyzedItemSummaryDto
{
    /// <summary>
    /// Gets or sets the Jellyfin item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the human-readable item name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item kind (e.g. "Episode", "Movie", "Series").
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parent series name when the item is an episode.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the season number when the item is an episode.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number when the item is an episode.
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the production year, when known.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the primary image tag for cache-busting image requests.
    /// </summary>
    public string? PrimaryImageTag { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether at least one provider produced results for this item.
    /// </summary>
    public bool HasSegments { get; set; }

    /// <summary>
    /// Gets or sets the most recent analysis timestamp across providers, when available.
    /// </summary>
    public DateTime? LastAnalyzedAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether any provider has a stored error for this item.
    /// Mirrors the <c>hasError</c> filter so a client can render the badge without a follow-up
    /// per-item request.
    /// </summary>
    public bool HasError { get; set; }

    /// <summary>
    /// Gets or sets the names of providers that have analyzed this item.
    /// </summary>
    public IReadOnlyList<string> Providers { get; set; } = [];
}
