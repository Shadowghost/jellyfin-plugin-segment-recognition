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
    /// Matched chapter name for the refined intro segment produced by black-frame analysis.
    /// </summary>
    internal const string BlackFrameIntro = "blackframe-intro";

    /// <summary>
    /// Matched chapter name for the refined outro segment produced by black-frame analysis.
    /// </summary>
    internal const string BlackFrameOutro = "blackframe-outro";

    /// <summary>
    /// Chromaprint fingerprint region name for intro analysis.
    /// </summary>
    internal const string RegionIntro = "Intro";

    /// <summary>
    /// Chromaprint fingerprint region name for credits analysis.
    /// </summary>
    internal const string RegionCredits = "Credits";

    /// <summary>
    /// Matched chapter name for segments imported from <c>.edl</c> sidecar files.
    /// </summary>
    internal const string EdlImportName = "edl-import";

    /// <summary>
    /// Matched chapter name for segments imported from the intro-skipper plugin database.
    /// </summary>
    internal const string IntroSkipperImportName = "intro-skipper import";

    /// <summary>
    /// Every sentinel <see cref="Data.Entities.ChapterAnalysisResult.MatchedChapterName"/> value
    /// owned by a provider other than <c>ChapterNameProvider</c>.
    /// </summary>
    internal static readonly string[] ForeignToChapterName =
    [
        ChromaprintIntro,
        ChromaprintCredits,
        ChromaprintPreview,
        BlackFrameIntro,
        BlackFrameOutro,
        BlackFramePreview,
        EdlImportName,
        IntroSkipperImportName,
    ];

    /// <summary>
    /// Sentinels owned by the black-frame provider.
    /// </summary>
    internal static readonly string[] BlackFrameOwned =
    [
        BlackFrameIntro,
        BlackFrameOutro,
        BlackFramePreview,
    ];

    /// <summary>
    /// Sentinels owned by the chromaprint provider.
    /// </summary>
    internal static readonly string[] ChromaprintOwned =
    [
        ChromaprintIntro,
        ChromaprintCredits,
        ChromaprintPreview,
    ];
}
