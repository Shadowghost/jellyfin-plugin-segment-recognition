namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Constants for <see cref="Data.Entities.ChapterAnalysisResult.MatchedChapterName"/> values
/// that identify which analysis pipeline produced a segment, and for
/// <see cref="Data.Entities.ChromaprintResult.Region"/> values.
/// </summary>
internal static class SegmentSourceNames
{
    /// <summary>
    /// Matched chapter name for chromaprint intro segment results.
    /// </summary>
    internal const string ChromaprintIntro = "chromaprint";

    /// <summary>
    /// Matched chapter name for chromaprint credits/outro segment results.
    /// </summary>
    internal const string ChromaprintCredits = "chromaprint-credits";

    /// <summary>
    /// Matched chapter name for preview segments inferred from chromaprint credits analysis.
    /// </summary>
    internal const string ChromaprintPreview = "chromaprint-preview";

    /// <summary>
    /// Matched chapter name for preview segments inferred from black-frame outro analysis.
    /// </summary>
    internal const string BlackFramePreview = "blackframe-preview";

    /// <summary>
    /// Chromaprint fingerprint region name for intro analysis.
    /// </summary>
    internal const string RegionIntro = "Intro";

    /// <summary>
    /// Chromaprint fingerprint region name for credits analysis.
    /// </summary>
    internal const string RegionCredits = "Credits";
}
