using System;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Normalized parameters for <see cref="SegmentDataQueryService.GetAnalyzedItemsAsync"/>.
/// The controller is responsible for clamping <see cref="StartIndex"/> and <see cref="Limit"/>.
/// </summary>
public sealed class AnalyzedItemsQuery
{
    /// <summary>Gets the optional parent identifier; only descendants are returned when set.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Gets a value restricting to items where any provider produced (true) or did not produce (false) results.</summary>
    public bool? HasSegments { get; init; }

    /// <summary>Gets the optional provider-name filter.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the optional lower bound on the most-recent analysis instant.</summary>
    public DateTime? AnalyzedSince { get; init; }

    /// <summary>Gets a value restricting to containers where some provider does (true) or does not (false) have a stored error. Evaluated after the container roll-up, not per provider row.</summary>
    public bool? HasError { get; init; }

    /// <summary>Gets the sort key: "name" (default) or "lastAnalyzed".</summary>
    public string? OrderBy { get; init; }

    /// <summary>Gets a value indicating whether the sort is reversed.</summary>
    public bool Descending { get; init; }

    /// <summary>Gets the number of results to skip.</summary>
    public int StartIndex { get; init; }

    /// <summary>Gets the maximum number of results to return.</summary>
    public int Limit { get; init; }
}
