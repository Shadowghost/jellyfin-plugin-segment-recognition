using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Resolves the ffmpeg executable to run, following the same precedence Jellyfin does.
/// </summary>
/// <remarks>
/// <see cref="IMediaEncoder.EncoderPath"/> is empty more often than it looks: Jellyfin only sets it
/// once its own validation has passed, so it is also empty when validation failed, when
/// <c>FFmpeg:novalidation</c> is set, and while the server is still starting up. None of those mean
/// "there is no ffmpeg" - Jellyfin's own last resort is the bare executable name, resolved through
/// <c>$PATH</c> - so treating an empty value as "no ffmpeg configured" disables analysis on an
/// install where ffmpeg works perfectly well.
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
    /// <returns>The validated path, the configured one, or the bare executable name.</returns>
    internal static string Resolve(IMediaEncoder mediaEncoder, IConfigurationManager configurationManager)
    {
        var validated = mediaEncoder.EncoderPath;
        if (!string.IsNullOrEmpty(validated))
        {
            return validated;
        }

        var configured = configurationManager.GetEncodingOptions()?.EncoderAppPath;
        return string.IsNullOrEmpty(configured) ? DefaultExecutable : configured;
    }
}
