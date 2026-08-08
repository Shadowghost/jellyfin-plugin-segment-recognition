namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Describes one of the plugin's segment providers: its canonical name (as stored in
/// <see cref="Data.Entities.AnalysisStatus.ProviderName"/> and accepted by the
/// <c>provider</c> query filter), a human-friendly label, and whether it is currently
/// enabled in the plugin configuration. Returned by <c>GET /SegmentRecognition/v1/Providers</c>
/// so clients don't have to hard-code provider names or reach into the core plugin config.
/// </summary>
public class ProviderInfoDto
{
    /// <summary>
    /// Gets or sets the canonical provider name (e.g. <c>ChapterName</c>).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-friendly display label (e.g. <c>Chapter names</c>).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the provider is currently enabled.
    /// </summary>
    public bool Enabled { get; set; }
}
