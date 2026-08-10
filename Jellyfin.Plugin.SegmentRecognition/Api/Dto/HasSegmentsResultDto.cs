using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Per-item entry in the bulk <c>GET /SegmentRecognition/v1/HasSegments</c> response.
/// </summary>
public class HasSegmentsResultDto
{
    /// <summary>
    /// Gets or sets the Jellyfin item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether any provider has stored results for this item.
    /// </summary>
    public bool HasSegments { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether any provider has run analysis on this item,
    /// regardless of whether results were produced. Distinguishes "analyzed with zero matches"
    /// (<see cref="Analyzed"/>=<c>true</c>, <see cref="HasSegments"/>=<c>false</c>) from
    /// "never analyzed" (both <c>false</c>) so the UI can render them differently.
    /// </summary>
    public bool Analyzed { get; set; }

    /// <summary>
    /// Gets or sets the provider names (e.g. "ChapterName", "BlackFrame") that have stored
    /// results for this item. Empty when <see cref="HasSegments"/> is <c>false</c>.
    /// </summary>
    public IReadOnlyList<string> Providers { get; set; } = [];
}
