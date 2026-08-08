using System;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Normalized parameters for <see cref="SegmentDataQueryService.SearchSegmentsAsync"/>.
/// The controller is responsible for parsing the segment-type name and clamping the paging values.
/// </summary>
public sealed class SegmentSearchQuery
{
    /// <summary>Gets the parsed integer segment-type filter, or <c>null</c> for any type.</summary>
    public int? TypeValue { get; init; }

    /// <summary>Gets the minimum segment duration in milliseconds.</summary>
    public long? MinDurationMs { get; init; }

    /// <summary>Gets the maximum segment duration in milliseconds.</summary>
    public long? MaxDurationMs { get; init; }

    /// <summary>Gets the optional parent identifier to constrain the search.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Gets a case-insensitive substring the matched source/chapter name must contain, or <c>null</c> for any.</summary>
    public string? NameContains { get; init; }

    /// <summary>Gets the sort key: "createdAt" (default), "duration", or "start".</summary>
    public string? OrderBy { get; init; }

    /// <summary>Gets a value indicating whether the sort is reversed. Default order is descending by creation time.</summary>
    public bool Descending { get; init; }

    /// <summary>Gets the number of rows to skip.</summary>
    public int StartIndex { get; init; }

    /// <summary>Gets the page size.</summary>
    public int Limit { get; init; }
}
