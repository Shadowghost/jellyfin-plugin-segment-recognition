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
    /// Part of the comparer's behaviour is driven by code the config-value hash cannot see - the
    /// inverted-index fuzzing strategy, the <c>MinShiftVotes</c> gate, the consensus rule. Bump
    /// this whenever a code change alters which segments the matcher produces, so existing
    /// chromaprint results are treated as stale and re-compared on the next analysis run
    /// (fingerprints are unaffected - only the comparison + refinement re-runs).
    /// </para>
    /// <para>
    /// v2: the index fuzz became arithmetic rather than a bit rotation, the index maps every
    /// occurrence instead of only the first, run length is counted in points, and a matched
    /// region now needs agreement from more than one counterpart.
    /// </para>
    /// <para>
    /// v3: counterparts are the nearest episodes rather than the first rows of the group,
    /// equally-supported candidate regions are separated by length instead of by earliest start,
    /// and intros/outros are pruned against the season's clustered positions (the thresholds that
    /// rule uses are code constants; only its on/off switch is a config value).
    /// </para>
    /// <para>
    /// v4: season position clusters chain on the previous member rather than the first, so a
    /// season whose cold open varies continuously is no longer split into pieces with its tail
    /// pruned. Bumped so the intros v3 discarded are recomputed - a prune deletes the segment, so
    /// nothing else would bring it back.
    /// </para>
    /// </summary>
    private const int ChromaprintComparisonAlgoVersion = 4;

    /// <summary>
    /// Version of the chromaprint fingerprint-generation algorithm, mixed into
    /// <see cref="ChromaprintIntro"/> and <see cref="ChromaprintCredits"/>.
    /// <para>
    /// Fingerprint output depends on ffmpeg extraction details that are not config values. Bump
    /// this when any of them change so stored fingerprints are regenerated. Like
    /// <see cref="BlackFrameExtraction"/>, fingerprint extraction is the expensive part, so the
    /// token is only mixed in once it moves past the v1 baseline: introducing the mechanism must
    /// not, by itself, invalidate every fingerprint in the library.
    /// </para>
    /// </summary>
    private const int ChromaprintFingerprintAlgoVersion = 1;

    /// <summary>
    /// The sample rate every fingerprint in the wild was generated at, back when the setting was
    /// not reachable from the configuration page. See <see cref="SampleRateFragment"/>.
    /// </summary>
    private const int DefaultChromaprintSampleRate = 22050;

    /// <summary>
    /// Hash of the settings that affect intro-region fingerprint generation.
    /// </summary>
    /// <remarks>
    /// The region bounds are code constants rather than config values now, and how much of an item
    /// is covered is tracked per fingerprint instead (see the region check in the analysis task).
    /// They are still hashed, at the values they have always had, so that existing fingerprints
    /// stay valid - dropping them from the input would change every hash and re-extract the whole
    /// library to record something that has not changed. Same reasoning as
    /// <see cref="SampleRateFragment"/>.
    /// </remarks>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string ChromaprintIntro(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"cp-intro|iap={ChromaprintRegions.IntroFraction}|cads={ChromaprintRegions.MaxIntroSeconds:F0}{SampleRateFragment(config)}");
        return ComputeHash(WithFingerprintVersion(input));
    }

    /// <summary>
    /// Hash of the config values that affect chromaprint fingerprint generation for the Credits region.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string ChromaprintCredits(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"cp-credits|cads={config.CreditsAnalysisDurationSeconds}|pad={config.ProbeAudioDuration}{SampleRateFragment(config)}");
        return ComputeHash(WithFingerprintVersion(input));
    }

    /// <summary>
    /// The sample-rate contribution to a fingerprint hash.
    /// </summary>
    /// <remarks>
    /// The sample rate is a config value and it changes the fingerprint bit-for-bit, so omitting it
    /// meant a rate change silently kept old fingerprints that were then compared against
    /// newly-generated, incompatible ones. But it only became reachable from the UI now, and
    /// mixing it into the hash unconditionally would invalidate every fingerprint on every
    /// existing install - hours of ffmpeg - to record a value that has not actually changed on any
    /// of them. So it contributes nothing while it sits at the historical default, exactly like
    /// <see cref="WithFingerprintVersion"/> stays silent at its v1 baseline.
    /// </remarks>
    private static string SampleRateFragment(PluginConfiguration config)
    {
        return config.ChromaprintSampleRate == DefaultChromaprintSampleRate
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"|sr={config.ChromaprintSampleRate}");
    }

    /// <summary>
    /// Hash of the config values that affect chromaprint comparison and refinement.
    /// Used for <see cref="Data.Entities.ChapterAnalysisResult"/> rows with chromaprint-derived segments.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string ChromaprintComparison(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // The matcher tuning knobs (bit errors, time skip, index shift) ARE config values and they
        // change which regions the comparer returns, so they are hashed explicitly. Only the
        // code-level behaviour that no config value describes is delegated to
        // ChromaprintComparisonAlgoVersion; bump that constant on any matcher change.
        // Min intro/outro also drive the comparer's min-match-duration now, so the intro/outro
        // mins in the hash already capture what ChromaprintMinMatchDurationSeconds used to.
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"cp-cmp|algo={ChromaprintComparisonAlgoVersion}|minI={config.MinIntroDurationSeconds}|maxI={config.MaxIntroDurationSeconds}|minO={config.MinOutroDurationSeconds}|maxO={config.MaxOutroDurationSeconds}"
            + $"|mbe={config.ChromaprintMaxBitErrors}|mts={config.ChromaprintMaxTimeSkipSeconds}|iis={config.ChromaprintInvertedIndexShift}"
            + $"|epi={config.EnablePreviewInference}|minP={config.MinPreviewDurationSeconds}|maxP={config.MaxPreviewDurationSeconds}"
            + $"|sop={config.EnableSeasonOutlierPruning}"
            + $"{RefinementFragment(config)}");
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
    /// Hash of the config values that affect black-frame <em>sample extraction</em> - the
    /// ffmpeg scan itself.
    /// <para>
    /// Intentionally a constant. Extraction is the single most expensive analysis step in the
    /// plugin - a full-episode ffmpeg scan can run for minutes - and invalidating the cache
    /// across a whole library because a knob moved by one is prohibitively costly. We
    /// deliberately break the "hash covers everything that affects output" contract here; the
    /// user-facing escape hatch is the <c>ReanalyzeBlackFrames</c> toggle in the config page,
    /// which force-clears the cache on demand.
    /// </para>
    /// <para>
    /// The knobs that only affect how cached samples are turned into segments live in
    /// <see cref="BlackFrameSegments"/> instead: those are cheap to re-apply from cache, so
    /// they get real staleness tracking.
    /// </para>
    /// </summary>
    /// <param name="config">The plugin configuration (unused by design - see remarks).</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string BlackFrameExtraction(PluginConfiguration config)
    {
        return ComputeHash("bf|v2");
    }

    /// <summary>
    /// Hash of the config values that turn cached black-frame samples into segments: clustering
    /// threshold, the intro/outro duration windows, preview inference, and the refinement
    /// pipeline settings.
    /// <para>
    /// Segments are now persisted with their refined boundaries rather than being re-derived on
    /// every query, so a change here has to invalidate the stored rows. Regenerating them only
    /// needs the cached samples plus the refinement passes - no black-frame ffmpeg scan - which
    /// is why this is tracked properly while <see cref="BlackFrameExtraction"/> is not.
    /// </para>
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A 16-character hex hash string.</returns>
    public static string BlackFrameSegments(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"bf-seg|mind={config.BlackFrameMinDurationMs}"
            + $"|minI={config.MinIntroDurationSeconds}|maxI={config.MaxIntroDurationSeconds}"
            + $"|minO={config.MinOutroDurationSeconds}|maxO={config.MaxOutroDurationSeconds}|maxMO={config.MaxMovieOutroDurationSeconds}"
            + $"|iap={ChromaprintRegions.IntroFraction}|oas={config.OutroAnalysisSeconds}"
            + $"|epi={config.EnablePreviewInference}|minP={config.MinPreviewDurationSeconds}|maxP={config.MaxPreviewDurationSeconds}"
            + $"{RefinementFragment(config)}");
        return ComputeHash(input);
    }

    /// <summary>
    /// The refinement-pipeline settings shared by every provider that snaps its boundaries.
    /// Factored out so the black-frame and chromaprint hashes can never drift apart on the
    /// half of the configuration they have in common.
    /// </summary>
    private static string RefinementFragment(PluginConfiguration config)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"|sr={config.EnableSilenceRefinement}|sdb={config.SilenceDetectNoisedB}|smd={config.SilenceDetectMinDurationSeconds}"
            + $"|ssi={config.SilenceSnapInwardSeconds}|sso={config.SilenceSnapOutwardSeconds}"
            + $"|cs={config.EnableChapterSnapping}|csw={config.ChapterSnapWindowSeconds}"
            + $"|ks={config.EnableKeyframeSnapping}|ksw={config.KeyframeSnapWindowSeconds}");
    }

    /// <summary>
    /// Appends the fingerprint algorithm version to a hash input, but only once it moves past the
    /// v1 baseline. This keeps the very first introduction of the token from changing existing
    /// fingerprint hashes (which would trigger a costly full-library re-extraction for no benefit),
    /// while still giving future fingerprint-generation changes a clean invalidation lever.
    /// </summary>
    private static string WithFingerprintVersion(string input)
    {
        return ChromaprintFingerprintAlgoVersion > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{input}|fpv={ChromaprintFingerprintAlgoVersion}")
            : input;
    }

    private static string ComputeHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 8);
    }
}
