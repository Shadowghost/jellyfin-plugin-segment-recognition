using System;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Wire DTO mirroring <see cref="Data.Entities.AnalysisStatus"/>.
/// </summary>
public class AnalysisStatusDto
{
    /// <summary>
    /// Gets or sets the provider that produced the analysis.
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the analysis was performed (UTC).
    /// </summary>
    public DateTime AnalyzedAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the provider produced any results.
    /// </summary>
    public bool HasResults { get; set; }

    /// <summary>
    /// Gets or sets the last error captured for this provider/item, if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when <see cref="LastError"/> was last updated.
    /// </summary>
    public DateTime? LastErrorAt { get; set; }

    /// <summary>
    /// Gets or sets why the last run did or did not produce an intro, as the
    /// <see cref="Data.Entities.SegmentMatchOutcome"/> name. <c>null</c> when the provider does
    /// not look for intros or the item predates outcome recording. Unlike
    /// <see cref="LastError"/>, a value here does not mean the run failed.
    /// </summary>
    public string? IntroOutcome { get; set; }

    /// <summary>
    /// Gets or sets why the last run did or did not produce an outro/credits segment, as the
    /// <see cref="Data.Entities.SegmentMatchOutcome"/> name. See <see cref="IntroOutcome"/>.
    /// </summary>
    public string? OutroOutcome { get; set; }
}
