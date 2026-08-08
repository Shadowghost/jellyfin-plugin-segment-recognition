using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Request body for bulk recalculation. Either <see cref="ItemIds"/> or <see cref="ParentId"/>
/// (or both) must be provided.
/// </summary>
public class RecalculateRequestDto
{
    /// <summary>
    /// Gets or sets explicit item identifiers (movies, episodes, or seasons) to recalculate.
    /// </summary>
    public IReadOnlyList<Guid>? ItemIds { get; set; }

    /// <summary>
    /// Gets or sets a parent container (library/folder/series/season). When set, every
    /// descendant Movie/Episode is added to the recalculation set.
    /// </summary>
    public Guid? ParentId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to clear cached analysis rows before re-running.
    /// Defaults to <c>true</c>; setting <c>false</c> only re-pushes already-cached segments to
    /// Jellyfin without re-analyzing.
    /// </summary>
    public bool ClearCache { get; set; } = true;

    /// <summary>
    /// Gets or sets the optional maximum number of leaves to process concurrently. When null,
    /// defaults to <see cref="Configuration.PluginConfiguration.MaxParallelGroups"/> clamped to [1,8].
    /// </summary>
    public int? MaxParallelism { get; set; }
}
