using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SegmentRecognition.Configuration;

/// <summary>
/// Plugin configuration for Segment Recognition.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the black frame provider is enabled.
    /// </summary>
    public bool EnableBlackFrameProvider { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the chapter name provider is enabled.
    /// </summary>
    public bool EnableChapterNameProvider { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the chromaprint provider is enabled.
    /// </summary>
    public bool EnableChromaprintProvider { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the EDL import provider is enabled.
    /// When enabled, <c>.edl</c> sidecar files next to media files are parsed for segment data.
    /// </summary>
    public bool EnableEdlImportProvider { get; set; } = true;

    /// <summary>
    /// Gets or sets the black frame detection threshold (0-100).
    /// </summary>
    public double BlackFrameThreshold { get; set; } = 90.0;

    /// <summary>
    /// Gets or sets the minimum duration in milliseconds for a black frame cluster to be considered a segment boundary.
    /// </summary>
    public int BlackFrameMinDurationMs { get; set; } = 500;

    /// <summary>
    /// Gets or sets the downscale resolution height for black frame analysis.
    /// Frames are scaled to this height on the GPU before analysis to reduce CPU work.
    /// Valid values: 0 (no scaling), 480, 720. Default is 480.
    /// </summary>
    public int BlackFrameAnalysisHeight { get; set; } = 480;

    /// <summary>
    /// Gets or sets the chapter name regex patterns that indicate an intro segment.
    /// </summary>
#pragma warning disable CA1819 // Properties should not return arrays - required for XML serialization
    public string[] IntroChapterNames { get; set; } =
    [
        "Intro",
        "Introduction",
        "Opening",
        "Opening Credits",
        "OP",
        "Apertura",
        "Ouverture",
        "Vorspann",
        "\u30AA\u30FC\u30D7\u30CB\u30F3\u30B0",
        "\u7247\u5934",
        "Intermission/Intro Animation"
    ];

    /// <summary>
    /// Gets or sets the chapter name regex patterns that indicate an outro segment.
    /// </summary>
    public string[] OutroChapterNames { get; set; } =
    [
        "Outro",
        "Ending",
        "Credits",
        "End Credits",
        "ED",
        "Cierre",
        "G\u00e9n\u00e9rique",
        "Abspann",
        "\u30A8\u30F3\u30C7\u30A3\u30F3\u30B0",
        "\u7247\u5C3E",
        "Endcards/Credits"
    ];

    /// <summary>
    /// Gets or sets the chapter name regex patterns that indicate a recap segment.
    /// </summary>
    public string[] RecapChapterNames { get; set; } =
    [
        "Recap",
        "Previously",
        "Previously on",
        "Summary",
        "Last time",
        "Catch up",
        "Catch-up",
        "Resumen",
        "R\u00e9sum\u00e9",
        "Zusammenfassung",
        "\u524D\u56DE",
        "Preview/Recap"
    ];

    /// <summary>
    /// Gets or sets the chapter name regex patterns that indicate a preview segment.
    /// </summary>
    public string[] PreviewChapterNames { get; set; } =
    [
        "Preview",
        "PV",
        "Next time",
        "Next on",
        "Next Episode",
        "Coming Up",
        "Coming Soon",
        "Sneak Peek",
        "Teaser",
        "Trailer",
        "Avance",
        "Aper\u00e7u",
        "Vorschau",
        "\u6B21\u56DE\u4E88\u544A"
    ];

    /// <summary>
    /// Gets or sets the chapter name regex patterns that indicate a commercial segment.
    /// Matches <see cref="Database.Implementations.Enums.MediaSegmentType.Commercial"/>.
    /// </summary>
    public string[] CommercialChapterNames { get; set; } =
    [
        "Commercial",
        "Commercials",
        "Commercial break",
        "Ad break",
        "Ad",
        "Advertisement",
        "Ads",
        "Werbung",
        "Werbespot",
        "Werbepause",
        "Publicidad",
        "Publicit\u00E9",
        "Pubblicit\u00E0",
        "CM",
        "\u5E83\u544A",
        "Sponsor",
        "selfpromo",
        "Unpaid/Self Promotion"
    ];

#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets the minimum intro duration in seconds.
    /// </summary>
    public int MinIntroDurationSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum intro duration in seconds.
    /// </summary>
    public int MaxIntroDurationSeconds { get; set; } = 240;

    /// <summary>
    /// Gets or sets the minimum outro/credits duration in seconds.
    /// </summary>
    public int MinOutroDurationSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the maximum outro/credits duration in seconds.
    /// </summary>
    public int MaxOutroDurationSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets the maximum outro/credits duration in seconds for movies.
    /// Movie credits are typically much longer than TV episode credits.
    /// </summary>
    public int MaxMovieOutroDurationSeconds { get; set; } = 900;

    /// <summary>
    /// Gets or sets the minimum commercial break duration in seconds.
    /// Short promos are typically 15 s; this lower bound catches the briefest ones while
    /// rejecting noise.
    /// </summary>
    public int MinCommercialDurationSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum commercial break duration in seconds.
    /// Typical broadcast ad pods run 90 – 180 s; this cap keeps accidental matches on
    /// feature-length chapters out of the Commercial bucket.
    /// </summary>
    public int MaxCommercialDurationSeconds { get; set; } = 180;

    /// <summary>
    /// Gets or sets the percentage of an episode to analyze from the start for intro detection (0.0-1.0).
    /// </summary>
    public double IntroAnalysisPercent { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the duration in seconds from the end of an episode to analyze for outro detection.
    /// </summary>
    public int OutroAnalysisSeconds { get; set; } = 240;

    /// <summary>
    /// Gets or sets the duration in seconds to analyze for chromaprint fingerprinting.
    /// Capped to the region determined by IntroAnalysisPercent.
    /// </summary>
    public int ChromaprintAnalysisDurationSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets the sample rate for chromaprint analysis.
    /// </summary>
    public int ChromaprintSampleRate { get; set; } = 22050;

    /// <summary>
    /// Gets or sets the maximum Hamming distance (in bits out of 32) for two fingerprint points to be considered matching.
    /// Lower values = stricter matching.
    /// </summary>
    public int ChromaprintMaxBitErrors { get; set; } = 6;

    /// <summary>
    /// Gets or sets the maximum time gap in seconds between consecutive matching fingerprint points
    /// before the match is considered broken.
    /// </summary>
    public double ChromaprintMaxTimeSkipSeconds { get; set; } = 3.5;

    /// <summary>
    /// Gets or sets the shift tolerance for the inverted index lookup during fingerprint comparison.
    /// Higher values find more potential alignments but are slower.
    /// </summary>
    public int ChromaprintInvertedIndexShift { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum number of groups (seasons/albums) to process in parallel during chromaprint comparison.
    /// </summary>
    public int MaxParallelGroups { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether a detected intro or outro is discarded when it sits
    /// at a position almost no other episode in the season shares.
    /// <para>
    /// An episode that has no intro of its own can still produce one, by matching an incidental
    /// music cue it happens to share with a sibling. Those land at scattered positions, while real
    /// segments cluster on the handful of positions the season's format uses - intros measured
    /// from the start of the file, outros from the end. Seasons with no dominant position are left
    /// alone, so shows whose placement genuinely varies are not affected.
    /// </para>
    /// </summary>
    public bool EnableSeasonOutlierPruning { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether silence-based refinement of segment boundaries is enabled.
    /// </summary>
    public bool EnableSilenceRefinement { get; set; } = true;

    /// <summary>
    /// Gets or sets the noise floor in dB for silence detection.
    /// </summary>
    public int SilenceDetectNoisedB { get; set; } = -50;

    /// <summary>
    /// Gets or sets the minimum silence duration in seconds for silence detection.
    /// </summary>
    public double SilenceDetectMinDurationSeconds { get; set; } = 0.33;

    /// <summary>
    /// Gets or sets the inward search window in seconds for silence-based boundary refinement.
    /// "Inward" means toward the center of the segment (later for start boundaries, earlier for end boundaries).
    /// A larger inward window reflects the likelihood that detected boundaries slightly overshoot the actual segment.
    /// </summary>
    public double SilenceSnapInwardSeconds { get; set; } = 5.0;

    /// <summary>
    /// Gets or sets the outward search window in seconds for silence-based boundary refinement.
    /// "Outward" means away from the segment center (earlier for start boundaries, later for end boundaries).
    /// A smaller outward window avoids snapping to silence gaps in adjacent content.
    /// </summary>
    public double SilenceSnapOutwardSeconds { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets a value indicating whether credits fingerprinting is enabled.
    /// </summary>
    public bool EnableCreditsFingerprinting { get; set; } = true;

    /// <summary>
    /// Gets or sets the duration in seconds from the end of an episode to analyze for credits fingerprinting.
    /// </summary>
    public int CreditsAnalysisDurationSeconds { get; set; } = 240;

    /// <summary>
    /// Gets or sets a value indicating whether to probe the actual audio stream duration via ffprobe
    /// before calculating the credits fingerprint region. Defaults to true: it guards against MKV
    /// containers whose duration is inflated by subtitle tracks that extend beyond the audio/video,
    /// which would otherwise make the credits region seek past the end of the audio. Not exposed in
    /// the UI; the extra ffprobe call is cheap and the correct behaviour for typical libraries.
    /// </summary>
    public bool ProbeAudioDuration { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to infer preview segments from credits that end before the episode's runtime.
    /// When enabled, if a detected outro/credits ends with a trailing portion between
    /// <see cref="MinPreviewDurationSeconds"/> and <see cref="MaxPreviewDurationSeconds"/>,
    /// the remaining portion is marked as a Preview segment. Trailing portions shorter than the
    /// minimum are absorbed into the outro instead.
    /// </summary>
    public bool EnablePreviewInference { get; set; }

    /// <summary>
    /// Gets or sets the minimum trailing duration (in seconds) after the credits to be treated
    /// as a Preview segment. Anything shorter is absorbed into the outro. Used only when
    /// <see cref="EnablePreviewInference"/> is enabled.
    /// </summary>
    public int MinPreviewDurationSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum trailing duration (in seconds) after the credits to be treated
    /// as a Preview segment. Anything longer is left untouched (probably a post-credits scene).
    /// Used only when <see cref="EnablePreviewInference"/> is enabled.
    /// </summary>
    public int MaxPreviewDurationSeconds { get; set; } = 45;

    /// <summary>
    /// Gets or sets a value indicating whether chapter snapping of segment boundaries is enabled.
    /// When enabled, detected boundaries are snapped to the nearest chapter marker within
    /// <see cref="ChapterSnapWindowSeconds"/>. Chapter markers are placed by content creators
    /// and are typically more precise than algorithmically detected boundaries.
    /// </summary>
    public bool EnableChapterSnapping { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum window in seconds to search for chapter markers near a segment boundary.
    /// </summary>
    public double ChapterSnapWindowSeconds { get; set; } = 5.0;

    /// <summary>
    /// Gets or sets a value indicating whether keyframe snapping of segment boundaries is enabled.
    /// </summary>
    public bool EnableKeyframeSnapping { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum window in seconds to search for keyframes near a segment boundary.
    /// </summary>
    public double KeyframeSnapWindowSeconds { get; set; } = 3.0;

    /// <summary>
    /// Gets or sets a value indicating whether to re-run black frame analysis for all items
    /// on the next scheduled task run. Clears existing black frame results and re-analyzes
    /// from scratch. Automatically reset to false after the task starts.
    /// </summary>
    public bool ReanalyzeBlackFrames { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to recreate all Jellyfin media segments
    /// from cached analysis data on the next scheduled task run.
    /// Automatically reset to false after the task completes.
    /// </summary>
    public bool ForceRegenerate { get; set; }
}
