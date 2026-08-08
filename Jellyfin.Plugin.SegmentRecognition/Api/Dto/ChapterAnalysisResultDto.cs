using System;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Wire DTO mirroring <see cref="Data.Entities.ChapterAnalysisResult"/>.
/// Timestamps are exposed in milliseconds rather than ticks for browser consumption.
/// </summary>
public class ChapterAnalysisResultDto
{
    /// <summary>
    /// Gets or sets the segment type as the <see cref="Database.Implementations.Enums.MediaSegmentType"/>
    /// enum name (e.g. "Intro", "Outro", "Recap").
    /// </summary>
    public string SegmentType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the segment start position in milliseconds.
    /// </summary>
    public double StartMs { get; set; }

    /// <summary>
    /// Gets or sets the segment end position in milliseconds.
    /// </summary>
    public double EndMs { get; set; }

    /// <summary>
    /// Gets or sets the chapter name that was matched to produce this segment.
    /// </summary>
    public string MatchedChapterName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configuration hash that was active when this result was created.
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp when this result was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
