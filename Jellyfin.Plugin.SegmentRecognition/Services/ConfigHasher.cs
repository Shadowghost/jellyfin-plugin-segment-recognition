using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.SegmentRecognition.Configuration;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Computes deterministic hashes of configuration subsets for staleness detection.
/// Each method covers exactly the config values that affect the corresponding analysis output.
/// </summary>
public static class ConfigHasher
{
    /// <summary>
    /// Version of the chromaprint comparison algorithm, mixed into <see cref="ChromaprintComparison"/>.
    /// <para>
    /// The comparer's behaviour is driven mostly by code - the inverted-index shift detection, the
    /// <c>MinShiftVotes</c> gate, and the frozen (non-config) bit-error/time-skip/index-shift
    /// parameters - none of which the config-value hash can see. Bump this whenever a code change
    /// alters which segments the matcher produces, so existing chromaprint results are treated as
    /// stale and re-compared on the next analysis run (fingerprints are unaffected - only the
    /// comparison + refinement re-runs).
    /// </para>
    /// </summary>
    private const int ChromaprintComparisonAlgoVersion = 1;

    /// <summary>
    /// Hash of the config values that affect chromaprint fingerprint generation for the Intro region.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string ChromaprintIntro(PluginConfiguration config)
    {
        // Sample rate is no longer configurable (hardcoded at 22050) so it's excluded from the hash.
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"cp-intro|iap={config.IntroAnalysisPercent}|cads={config.ChromaprintAnalysisDurationSeconds}");
        return ComputeHash(input);
    }

    /// <summary>
    /// Hash of the config values that affect chromaprint fingerprint generation for the Credits region.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string ChromaprintCredits(PluginConfiguration config)
    {
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"cp-credits|cads={config.CreditsAnalysisDurationSeconds}|pad={config.ProbeAudioDuration}");
        return ComputeHash(input);
    }

    /// <summary>
    /// Hash of the config values that affect chromaprint comparison and refinement.
    /// Used for <see cref="Data.Entities.ChapterAnalysisResult"/> rows with chromaprint-derived segments.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string ChromaprintComparison(PluginConfiguration config)
    {
        // The frozen chromaprint algorithm parameters (bit errors, time skip, index shift) and the
        // code-level comparer behaviour aren't config values, so they can't be hashed directly -
        // ChromaprintComparisonAlgoVersion stands in for them. Bump that constant on any matcher
        // change that affects output.
        // Min intro/outro also drive the comparer's min-match-duration now, so the intro/outro
        // mins in the hash already capture what ChromaprintMinMatchDurationSeconds used to.
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"cp-cmp|algo={ChromaprintComparisonAlgoVersion}|minI={config.MinIntroDurationSeconds}|maxI={config.MaxIntroDurationSeconds}|minO={config.MinOutroDurationSeconds}|maxO={config.MaxOutroDurationSeconds}"
            + $"|sr={config.EnableSilenceRefinement}|sdb={config.SilenceDetectNoisedB}|smd={config.SilenceDetectMinDurationSeconds}|ssi={config.SilenceSnapInwardSeconds}|sso={config.SilenceSnapOutwardSeconds}"
            + $"|cs={config.EnableChapterSnapping}|csw={config.ChapterSnapWindowSeconds}"
            + $"|ks={config.EnableKeyframeSnapping}|ksw={config.KeyframeSnapWindowSeconds}"
            + $"|epi={config.EnablePreviewInference}|minP={config.MinPreviewDurationSeconds}|maxP={config.MaxPreviewDurationSeconds}");
        return ComputeHash(input);
    }

    /// <summary>
    /// Hash of the config values that affect chapter name matching.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string ChapterName(PluginConfiguration config)
    {
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"ch|intro={string.Join(",", config.IntroChapterNames)}"
            + $"|outro={string.Join(",", config.OutroChapterNames)}"
            + $"|recap={string.Join(",", config.RecapChapterNames)}"
            + $"|preview={string.Join(",", config.PreviewChapterNames)}"
            + $"|commercial={string.Join(",", config.CommercialChapterNames)}"
            + $"|minI={config.MinIntroDurationSeconds}|maxI={config.MaxIntroDurationSeconds}"
            + $"|minO={config.MinOutroDurationSeconds}|maxO={config.MaxOutroDurationSeconds}|maxMO={config.MaxMovieOutroDurationSeconds}"
            + $"|minC={config.MinCommercialDurationSeconds}|maxC={config.MaxCommercialDurationSeconds}");
        return ComputeHash(input);
    }

    /// <summary>
    /// Hash of the config values that affect black frame detection.
    /// <para>
    /// Intentionally a constant. Black-frame sample extraction is the single most expensive
    /// analysis step in the plugin - a single full-episode ffmpeg scan can run for minutes,
    /// and invalidating the cache across a whole library because a knob moved by one is
    /// prohibitively costly. We deliberately break the "hash covers everything that affects
    /// output" contract here; the user-facing escape hatch is the <c>ReanalyzeBlackFrames</c>
    /// toggle in the config page, which force-clears the cache on demand.
    /// </para>
    /// </summary>
    /// <param name="config">The plugin configuration (unused by design - see remarks).</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string BlackFrame(PluginConfiguration config)
    {
        return ComputeHash("bf|v2");
    }

    private static string ComputeHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 8);
    }
}
