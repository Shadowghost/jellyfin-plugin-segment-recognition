using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Resolves the ffmpeg executable to run, following the same precedence Jellyfin does.
/// </summary>
/// <remarks>
/// <see cref="IMediaEncoder.EncoderPath"/> is empty more often than it looks: Jellyfin only sets it
/// once its own validation has passed, so it is also empty when validation failed, when
/// <c>FFmpeg:novalidation</c> is set, and for the whole of hosted-service startup, which runs
/// before the server resolves ffmpeg at all. None of those mean "there is no ffmpeg" - Jellyfin's
/// own last resort is the bare executable name, resolved through <c>$PATH</c> - so treating an
/// empty value as "no ffmpeg configured" disables analysis on an install where ffmpeg works.
/// </remarks>
internal static class FfmpegPathResolver
{
    /// <summary>
    /// The executable name Jellyfin falls back to, resolved by the OS through <c>$PATH</c>.
    /// </summary>
    internal const string DefaultExecutable = "ffmpeg";

    /// <summary>
    /// Resolves the ffmpeg executable to run.
    /// </summary>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="configurationManager">The configuration manager, for the encoding options.</param>
    /// <returns>The validated path, a previously validated one, the configured one, or the bare name.</returns>
    internal static string Resolve(IMediaEncoder mediaEncoder, IConfigurationManager configurationManager)
    {
        var validated = mediaEncoder.EncoderPath;
        if (!string.IsNullOrEmpty(validated))
        {
            return validated;
        }

        var options = configurationManager.GetEncodingOptions();

        // Jellyfin writes the path it resolved into EncoderAppPathDisplay, and only once the path
        // has passed validation, so it survives a restart and names the executable actually in use
        // even when that came from the --ffmpeg switch. EncoderAppPath holds whatever was last
        // typed into the settings page, which can be stale or point at nothing.
        var lastValidated = options?.EncoderAppPathDisplay;
        if (!string.IsNullOrEmpty(lastValidated))
        {
            return lastValidated;
        }

        var configured = options?.EncoderAppPath;
        return string.IsNullOrEmpty(configured) ? DefaultExecutable : configured;
    }
}
