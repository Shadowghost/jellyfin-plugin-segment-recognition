using System;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Wire DTO mirroring <see cref="Data.Entities.ChromaprintResult"/>. The raw fingerprint bytes
/// are intentionally omitted; only their length is exposed.
/// </summary>
public class ChromaprintResultDto
{
    /// <summary>
    /// Gets or sets the region the fingerprint covers (e.g. "Intro" or "Credits").
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season identifier the fingerprint belongs to.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the duration that was analyzed, in seconds.
    /// </summary>
    public int AnalysisDurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the length of the raw fingerprint blob in bytes.
    /// </summary>
    public int FingerprintLength { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when this result was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
