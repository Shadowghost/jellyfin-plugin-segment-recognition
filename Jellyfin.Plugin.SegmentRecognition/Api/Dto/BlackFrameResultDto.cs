using System;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Wire DTO mirroring <see cref="Data.Entities.BlackFrameResult"/>.
/// </summary>
public class BlackFrameResultDto
{
    /// <summary>
    /// Gets or sets the timestamp of the detected black frame in milliseconds.
    /// </summary>
    public double TimestampMs { get; set; }

    /// <summary>
    /// Gets or sets the percentage of black pixels in the frame (0-100).
    /// </summary>
    public double BlackPercentage { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when this result was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
