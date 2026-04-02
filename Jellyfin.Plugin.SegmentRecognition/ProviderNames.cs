namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Provider name constants used as <see cref="Data.Entities.AnalysisStatus.ProviderName"/> values
/// and for per-library provider disable checks.
/// </summary>
internal static class ProviderNames
{
    /// <summary>
    /// The chapter-name provider.
    /// </summary>
    internal const string ChapterName = "ChapterName";

    /// <summary>
    /// The black-frame provider.
    /// </summary>
    internal const string BlackFrame = "BlackFrame";

    /// <summary>
    /// The chromaprint provider.
    /// </summary>
    internal const string Chromaprint = "Chromaprint";

    /// <summary>
    /// The EDL import provider.
    /// </summary>
    internal const string EdlImport = "EdlImport";
}
