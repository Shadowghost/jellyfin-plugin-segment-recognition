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
        // Chromaprint algorithm parameters (bit errors, time skip, index shift) are no longer
        // configurable so they're excluded from the hash.
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"cp-cmp|mmd={config.ChromaprintMinMatchDurationSeconds}"
            + $"|minI={config.MinIntroDurationSeconds}|maxI={config.MaxIntroDurationSeconds}|minO={config.MinOutroDurationSeconds}|maxO={config.MaxOutroDurationSeconds}"
            + $"|sr={config.EnableSilenceRefinement}|sdb={config.SilenceDetectNoisedB}|smd={config.SilenceDetectMinDurationSeconds}|ssi={config.SilenceSnapInwardSeconds}|sso={config.SilenceSnapOutwardSeconds}"
            + $"|cs={config.EnableChapterSnapping}|csw={config.ChapterSnapWindowSeconds}"
            + $"|ks={config.EnableKeyframeSnapping}|ksw={config.KeyframeSnapWindowSeconds}");
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
            + $"|minI={config.MinIntroDurationSeconds}|maxI={config.MaxIntroDurationSeconds}"
            + $"|minO={config.MinOutroDurationSeconds}|maxO={config.MaxOutroDurationSeconds}|maxMO={config.MaxMovieOutroDurationSeconds}");
        return ComputeHash(input);
    }

    /// <summary>
    /// Hash of the config values that affect black frame detection.
    /// Black frame threshold and analysis height are no longer configurable, so this hash
    /// is now a constant. It exists to maintain the staleness detection interface.
    /// </summary>
    /// <param name="config">The plugin configuration (unused — all black frame parameters are now fixed).</param>
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
