namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// What the installed ffmpeg can do, probed once at startup.
/// </summary>
/// <param name="Chromaprint">Whether the chromaprint muxer exists.</param>
/// <param name="RawFingerprints">Whether that muxer understands <c>-fp_format raw</c>.</param>
/// <param name="SilenceDetect">Whether the <c>silencedetect</c> filter exists.</param>
/// <param name="BlackFrame">Whether the <c>blackframe</c> filter exists.</param>
public sealed record FfmpegCapabilities(
    bool Chromaprint,
    bool RawFingerprints,
    bool SilenceDetect,
    bool BlackFrame)
{
    /// <summary>
    /// Gets a value indicating whether audio fingerprinting can run at all.
    /// </summary>
    public bool CanFingerprint => Chromaprint && RawFingerprints;

    /// <summary>
    /// Gets the capabilities assumed when the probe itself could not run.
    /// </summary>
    /// <remarks>
    /// Everything is treated as present, so a probe that fails for its own reasons - no encoder
    /// path configured yet, a permissions problem - never disables analysis that would have worked.
    /// </remarks>
    public static FfmpegCapabilities Unknown { get; } = new(true, true, true, true);
}
