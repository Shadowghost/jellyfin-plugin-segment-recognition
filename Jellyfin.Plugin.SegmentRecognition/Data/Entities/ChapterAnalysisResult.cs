using System;

namespace Jellyfin.Plugin.SegmentRecognition.Data.Entities;

/// <summary>
/// Stores a segment derived from chapter name matching or chromaprint analysis.
/// Compound key: (ItemId, SegmentType, MatchedChapterName).
/// </summary>
public class ChapterAnalysisResult
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the segment type as an integer.
    /// </summary>
    public int SegmentType { get; set; }

    /// <summary>
    /// Gets or sets the start position in ticks.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the end position in ticks.
    /// </summary>
    public long EndTicks { get; set; }

    /// <summary>
    /// Gets or sets the chapter name that was matched.
    /// </summary>
    public required string MatchedChapterName { get; set; }

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
