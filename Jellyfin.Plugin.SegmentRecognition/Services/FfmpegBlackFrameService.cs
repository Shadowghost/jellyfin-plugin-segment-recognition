using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Runs ffmpeg blackframe detection and crop detection on media files.
/// Supports hardware-accelerated decoding when configured in Jellyfin's encoding settings.
/// </summary>
public partial class FfmpegBlackFrameService
{
    /// <summary>
    /// Duration in seconds to sample from the middle of the file for crop detection.
    /// </summary>
    private const double CropDetectSampleSeconds = 10.0;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<FfmpegBlackFrameService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegBlackFrameService"/> class.
    /// </summary>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public FfmpegBlackFrameService(
        IMediaEncoder mediaEncoder,
        IConfigurationManager configurationManager,
        ILogger<FfmpegBlackFrameService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Detects the active video area by running ffmpeg's cropdetect filter on a sample
    /// from the middle of the file. Returns null if crop detection fails or the
    /// entire frame is active (no letterboxing).
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="runtimeSeconds">Total runtime of the file in seconds.</param>
    /// <param name="videoCodec">The video codec name (e.g. "h264", "hevc") for hardware acceleration eligibility.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Crop rectangle (width, height, x, y) or null if no letterboxing detected.</returns>
    public async Task<(int Width, int Height, int X, int Y)?> DetectCropAsync(
        string filePath,
        double runtimeSeconds,
        string? videoCodec,
        CancellationToken cancellationToken)
    {
        // Sample from 40% into the file to avoid cold opens / end credits
        var sampleStart = runtimeSeconds * 0.4;
        var sampleDuration = Math.Min(CropDetectSampleSeconds, runtimeSeconds - sampleStart);
        if (sampleDuration <= 0)
        {
            return null;
        }

        var stderr = await RunFfmpegWithHwFallbackAsync(
            videoCodec,
            0,
            (hwArgs, filterPrefix) =>
            {
                var a = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}-ss {1} -t {2} -i \"{3}\" -vf \"{4}cropdetect=round=2\" -an -sn -dn -f null -",
                    hwArgs,
                    sampleStart,
                    sampleDuration,
                    filePath,
                    filterPrefix);
                _logger.LogDebug("Running ffmpeg cropdetect: {Args}", a);
                return a;
            },
            cancellationToken).ConfigureAwait(false);

        // Parse the last cropdetect line — it's the most stable value after convergence.
        (int Width, int Height, int X, int Y)? lastCrop = null;
        foreach (Match match in CropDetectRegex().Matches(stderr))
        {
            if (!int.TryParse(match.Groups["w"].Value, CultureInfo.InvariantCulture, out var w)
                || !int.TryParse(match.Groups["h"].Value, CultureInfo.InvariantCulture, out var h)
                || !int.TryParse(match.Groups["x"].Value, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(match.Groups["y"].Value, CultureInfo.InvariantCulture, out var y))
            {
                continue;
            }

            lastCrop = (w, h, x, y);
        }

        if (lastCrop is null)
        {
            _logger.LogDebug("Cropdetect produced no output for {File}", filePath);
            return null;
        }

        // If x=0 and y=0 the crop is likely the full frame — no letterboxing
        if (lastCrop.Value.X == 0 && lastCrop.Value.Y == 0)
        {
            _logger.LogDebug(
                "No letterboxing detected for {File} (crop={W}:{H}:{X}:{Y})",
                filePath,
                lastCrop.Value.Width,
                lastCrop.Value.Height,
                lastCrop.Value.X,
                lastCrop.Value.Y);
            return null;
        }

        _logger.LogInformation(
            "Detected letterboxing for {File}: crop={W}:{H}:{X}:{Y}",
            filePath,
            lastCrop.Value.Width,
            lastCrop.Value.Height,
            lastCrop.Value.X,
            lastCrop.Value.Y);

        return lastCrop;
    }

    /// <summary>
    /// Detects black frames in a specific time range of a media file.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="threshold">Black frame threshold (0-100).</param>
    /// <param name="startSeconds">Start time in seconds to begin scanning.</param>
    /// <param name="durationSeconds">Duration in seconds to scan.</param>
    /// <param name="crop">Optional crop rectangle to apply before black frame detection (excludes letterbox bars).</param>
    /// <param name="sourceHeight">The source video height in pixels, used to skip downscaling when already at or below analysis resolution.</param>
    /// <param name="analysisHeight">Target height for downscaling (0 to disable). Frames are scaled to this height before the blackframe filter.</param>
    /// <param name="videoCodec">The video codec name (e.g. "h264", "hevc") for hardware acceleration eligibility.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of detected black frames with timestamp and black percentage.</returns>
    public async Task<List<(long TimestampTicks, double BlackPercentage)>> DetectBlackFramesAsync(
        string filePath,
        double threshold,
        double startSeconds,
        double durationSeconds,
        (int Width, int Height, int X, int Y)? crop,
        int sourceHeight,
        int analysisHeight,
        string? videoCodec,
        CancellationToken cancellationToken)
    {
        // Always use GPU scale when possible — even with crop. When crop is needed, we scale
        // the crop coordinates proportionally to match the GPU-scaled resolution, then apply
        // crop on CPU after hwdownload. This is much faster than downloading full-resolution
        // frames: e.g. for 4K with letterboxing, hwdownload transfers 853x480 instead of 3840x2160.
        var needsScale = analysisHeight > 0 && sourceHeight > analysisHeight;
        var gpuScaleHeight = needsScale ? analysisHeight : 0;

        // Pre-compute scaled crop coordinates for use inside the lambda
        (int Width, int Height, int X, int Y)? scaledCrop = null;
        if (crop.HasValue && needsScale)
        {
            var ratio = analysisHeight / (double)sourceHeight;
            scaledCrop = (
                (int)Math.Round(crop.Value.Width * ratio),
                (int)Math.Round(crop.Value.Height * ratio),
                (int)Math.Round(crop.Value.X * ratio),
                (int)Math.Round(crop.Value.Y * ratio));
        }
        else if (crop.HasValue)
        {
            scaledCrop = crop;
        }

        var stderr = await RunFfmpegWithHwFallbackAsync(
            videoCodec,
            gpuScaleHeight,
            (hwArgs, hwFilterPrefix) =>
            {
                var filterChain = new StringBuilder();
                filterChain.Append(hwFilterPrefix);

                if (scaledCrop.HasValue)
                {
                    filterChain.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "crop={0}:{1}:{2}:{3},",
                        scaledCrop.Value.Width,
                        scaledCrop.Value.Height,
                        scaledCrop.Value.X,
                        scaledCrop.Value.Y);
                }

                filterChain.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "blackframe=threshold={0}",
                    threshold);

                var a = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}-ss {1} -t {2} -i \"{3}\" -vf \"{4}\" -an -sn -dn -f null -",
                    hwArgs,
                    startSeconds,
                    durationSeconds,
                    filePath,
                    filterChain);
                _logger.LogDebug("Running ffmpeg black frame detection: {Args}", a);
                return a;
            },
            cancellationToken).ConfigureAwait(false);

        var results = new List<(long TimestampTicks, double BlackPercentage)>();
        foreach (Match match in BlackFrameRegex().Matches(stderr))
        {
            if (!double.TryParse(match.Groups["pblack"].Value, CultureInfo.InvariantCulture, out var pblack)
                || !double.TryParse(match.Groups["t"].Value, CultureInfo.InvariantCulture, out var t))
            {
                continue;
            }

            var absoluteSeconds = t + startSeconds;
            var ticks = (long)(absoluteSeconds * TimeSpan.TicksPerSecond);
            results.Add((ticks, pblack));
        }

        _logger.LogDebug(
            "Detected {Count} black frames in {File} (range {Start}s-{End}s)",
            results.Count,
            filePath,
            startSeconds,
            startSeconds + durationSeconds);

        return results;
    }

    /// <summary>
    /// Builds hwaccel input arguments and corresponding filter prefix based on Jellyfin's encoding configuration.
    /// Uses proper <c>-init_hw_device</c> initialization matching Jellyfin's transcoding pipeline for reliable
    /// device selection. Returns empty strings if no hardware acceleration is available.
    /// </summary>
    /// <param name="videoCodec">The video codec of the input file (e.g. "h264", "hevc").</param>
    /// <param name="scaleHeight">Optional target height for GPU-side scaling. When set and the source is taller,
    /// frames are scaled on the GPU before hwdownload, significantly reducing CPU work for downstream filters.
    /// Pass 0 to disable scaling (e.g. for cropdetect which needs full resolution).</param>
    /// <returns>A tuple of (hwaccel args to prepend before input, filter prefix to prepend before video filters).</returns>
    private (string HwArgs, string FilterPrefix) GetHwAccelArgs(string? videoCodec, int scaleHeight = 0)
    {
        var encodingOptions = _configurationManager.GetEncodingOptions();
        var hwType = encodingOptions.HardwareAccelerationType;

        if (hwType == HardwareAccelerationType.none)
        {
            return (string.Empty, GetSoftwareFilterPrefix(scaleHeight));
        }

        // Respect the user's configured hardware decoding codec list
        if (!string.IsNullOrEmpty(videoCodec)
            && encodingOptions.HardwareDecodingCodecs.Length > 0
            && !Array.Exists(
                encodingOptions.HardwareDecodingCodecs,
                c => string.Equals(c, videoCodec, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogDebug(
                "Video codec {Codec} is not in the hardware decoding codec list, falling back to software decoding",
                videoCodec);
            return (string.Empty, GetSoftwareFilterPrefix(scaleHeight));
        }

        // Map hw type to ffmpeg hwaccel name for SupportsHwaccel check
        var hwaccelName = hwType switch
        {
            HardwareAccelerationType.vaapi => "vaapi",
            HardwareAccelerationType.nvenc => "cuda",
            HardwareAccelerationType.qsv => "qsv",
            HardwareAccelerationType.videotoolbox => "videotoolbox",
            _ => null
        };

        if (hwaccelName is null || !_mediaEncoder.SupportsHwaccel(hwaccelName))
        {
            _logger.LogDebug("Hardware acceleration {HwType} is not supported by ffmpeg, falling back to software decoding", hwType);
            return (string.Empty, GetSoftwareFilterPrefix(scaleHeight));
        }

        var vaapiDevice = encodingOptions.VaapiDevice;
        if (string.IsNullOrEmpty(vaapiDevice))
        {
            vaapiDevice = "/dev/dri/renderD128";
        }

        // Build device init + hwaccel args following Jellyfin's transcoding pipeline patterns.
        // Proper -init_hw_device ensures reliable device selection on multi-GPU systems.
        // -filter_hw_device tells GPU-side scale filters which device context to use.
        var hwArgs = hwType switch
        {
            // QSV on Linux: derive from VAAPI for reliable device init
            // QSV on Windows: would derive from D3D11VA, but we use simple init as fallback
            HardwareAccelerationType.qsv => OperatingSystem.IsLinux()
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "-init_hw_device vaapi=va:{0} -init_hw_device qsv=qs@va -filter_hw_device qs -hwaccel qsv -hwaccel_output_format qsv ",
                    vaapiDevice)
                : "-init_hw_device qsv=qs -filter_hw_device qs -hwaccel qsv -hwaccel_output_format qsv ",

            HardwareAccelerationType.vaapi => string.Format(
                CultureInfo.InvariantCulture,
                "-init_hw_device vaapi=va:{0} -filter_hw_device va -hwaccel vaapi -hwaccel_output_format vaapi ",
                vaapiDevice),

            HardwareAccelerationType.nvenc =>
                "-init_hw_device cuda=cu:0 -filter_hw_device cu -hwaccel cuda -hwaccel_output_format cuda ",

            // VideoToolbox does not require -filter_hw_device
            HardwareAccelerationType.videotoolbox =>
                "-init_hw_device videotoolbox=vt -hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld ",

            _ => string.Empty
        };

        // Build the filter prefix: GPU-side scale (optional) → hwdownload → format.
        // The scale happens on the GPU before hwdownload so the CPU only sees small frames.
        // hwdownload is always needed because blackframe/cropdetect are CPU-only filters.
        var filterPrefix = (hwType, scaleHeight > 0) switch
        {
            (HardwareAccelerationType.qsv, true) => string.Format(
                CultureInfo.InvariantCulture,
                "vpp_qsv=h={0}:w=-1:format=nv12,hwdownload,format=nv12,",
                scaleHeight),
            (HardwareAccelerationType.qsv, false) =>
                "vpp_qsv=format=nv12,hwdownload,format=nv12,",

            (HardwareAccelerationType.vaapi, true) => string.Format(
                CultureInfo.InvariantCulture,
                "scale_vaapi=h={0}:w=-2:format=nv12,hwdownload,format=nv12,",
                scaleHeight),
            (HardwareAccelerationType.vaapi, false) =>
                "hwdownload,format=nv12,",

            (HardwareAccelerationType.nvenc, true) => string.Format(
                CultureInfo.InvariantCulture,
                "scale_cuda=-2:{0}:format=nv12,hwdownload,format=nv12,",
                scaleHeight),
            (HardwareAccelerationType.nvenc, false) =>
                "hwdownload,format=nv12,",

            (HardwareAccelerationType.videotoolbox, true) => string.Format(
                CultureInfo.InvariantCulture,
                "scale_vt=h={0}:w=-2,hwdownload,format=nv12,",
                scaleHeight),
            (HardwareAccelerationType.videotoolbox, false) =>
                "hwdownload,format=nv12,",
            _ => string.Empty
        };

        return (hwArgs, filterPrefix);
    }

    /// <summary>
    /// Returns the CPU-side filter prefix for software decoding (no hardware acceleration).
    /// </summary>
    private static string GetSoftwareFilterPrefix(int scaleHeight)
    {
        return scaleHeight > 0
            ? string.Format(CultureInfo.InvariantCulture, "scale=-2:{0},", scaleHeight)
            : string.Empty;
    }

    /// <summary>
    /// Runs an ffmpeg command that uses hardware acceleration, automatically falling back to
    /// software decoding if the hardware-accelerated command fails (non-zero exit code).
    /// Throws <see cref="InvalidOperationException"/> if the final attempt also fails, so that
    /// callers do not silently treat a failed run as "no results found".
    /// </summary>
    /// <param name="videoCodec">The video codec name for hardware acceleration eligibility.</param>
    /// <param name="scaleHeight">Optional GPU-side scale height (0 to disable). Passed to <see cref="GetHwAccelArgs"/>.</param>
    /// <param name="buildArgs">A function that takes (hwArgs, filterPrefix) and returns the full ffmpeg argument string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stderr output from the successful ffmpeg run.</returns>
    /// <exception cref="InvalidOperationException">Thrown when ffmpeg fails after all retry attempts.</exception>
    private async Task<string> RunFfmpegWithHwFallbackAsync(
        string? videoCodec,
        int scaleHeight,
        Func<string, string, string> buildArgs,
        CancellationToken cancellationToken)
    {
        var (hwArgs, filterPrefix) = GetHwAccelArgs(videoCodec, scaleHeight);
        var args = buildArgs(hwArgs, filterPrefix);

        var (stderr, exitCode) = await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);

        if (exitCode != 0 && hwArgs.Length > 0)
        {
            _logger.LogWarning("Hardware-accelerated ffmpeg failed (exit code {ExitCode}), retrying with software decoding", exitCode);
            args = buildArgs(string.Empty, GetSoftwareFilterPrefix(scaleHeight));
            (stderr, exitCode) = await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg failed with exit code {exitCode}: {args}");
        }

        return stderr;
    }

    private async Task<(string Stderr, int ExitCode)> RunFfmpegAsync(string args, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            Arguments = "-nostdin -hide_banner " + args,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Failed to set ffmpeg process priority to BelowNormal");
        }

        try
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ffmpeg exited with code {ExitCode}: {Args}", process.ExitCode, args);
            }

            return (stderr, process.ExitCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EnsureProcessKilled(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            EnsureProcessKilled(process);
            throw;
        }
    }

    /// <summary>
    /// Kills the process and its entire process tree if it hasn't already exited.
    /// Process.Dispose() does NOT kill the child process — it must be done explicitly.
    /// </summary>
    private static void EnsureProcessKilled(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill — safe to ignore.
        }
    }

    /// <summary>
    /// Detects silence intervals in a specific time range of a media file using ffmpeg's silencedetect filter.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="startSeconds">Start time in seconds to begin scanning.</param>
    /// <param name="durationSeconds">Duration in seconds to scan.</param>
    /// <param name="noisedB">Noise floor in dB for silence detection (e.g. -50).</param>
    /// <param name="minDurationSeconds">Minimum silence duration in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of detected silence intervals as (StartSeconds, EndSeconds) tuples.</returns>
    public virtual async Task<List<(double StartSeconds, double EndSeconds)>> DetectSilenceAsync(
        string filePath,
        double startSeconds,
        double durationSeconds,
        int noisedB,
        double minDurationSeconds,
        CancellationToken cancellationToken)
    {
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-ss {0} -t {1} -i \"{2}\" -af \"silencedetect=noise={3}dB:d={4}\" -vn -sn -dn -f null -",
            startSeconds,
            durationSeconds,
            filePath,
            noisedB,
            minDurationSeconds);

        _logger.LogDebug("Running ffmpeg silencedetect: {Args}", args);

        var (stderr, _) = await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);

        var results = new List<(double StartSeconds, double EndSeconds)>();
        var startMatches = SilenceStartRegex().Matches(stderr);
        var endMatches = SilenceEndRegex().Matches(stderr);

        for (int i = 0; i < endMatches.Count; i++)
        {
            if (!double.TryParse(endMatches[i].Groups["t"].Value, CultureInfo.InvariantCulture, out var end))
            {
                continue;
            }

            var adjustedEnd = end + startSeconds;

            if (i < startMatches.Count
                && double.TryParse(startMatches[i].Groups["t"].Value, CultureInfo.InvariantCulture, out var start))
            {
                results.Add((start + startSeconds, adjustedEnd));
            }
        }

        _logger.LogDebug(
            "Detected {Count} silence intervals in {File} (range {Start}s-{End}s)",
            results.Count,
            filePath,
            startSeconds,
            startSeconds + durationSeconds);

        return results;
    }

    [GeneratedRegex(@"pblack:(?<pblack>\d+).*?t:(?<t>[\d.]+)", RegexOptions.Compiled)]
    private static partial Regex BlackFrameRegex();

    [GeneratedRegex(@"crop=(?<w>\d+):(?<h>\d+):(?<x>\d+):(?<y>\d+)", RegexOptions.Compiled)]
    private static partial Regex CropDetectRegex();

    [GeneratedRegex(@"silence_start:\s*(?<t>[\d.]+)", RegexOptions.Compiled)]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s*(?<t>[\d.]+)", RegexOptions.Compiled)]
    private static partial Regex SilenceEndRegex();
}
