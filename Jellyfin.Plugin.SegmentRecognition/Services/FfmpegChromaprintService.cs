using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Runs ffmpeg chromaprint fingerprinting on a media file.
/// </summary>
public class FfmpegChromaprintService
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegChromaprintService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegChromaprintService"/> class.
    /// </summary>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="logger">The logger.</param>
    public FfmpegChromaprintService(IMediaEncoder mediaEncoder, ILogger<FfmpegChromaprintService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Generates a chromaprint fingerprint for a time range of the specified media file.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="sampleRate">Audio sample rate.</param>
    /// <param name="startSeconds">Start offset in seconds.</param>
    /// <param name="durationSeconds">Duration to analyze in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw fingerprint bytes, or empty array if generation failed.</returns>
    public async Task<byte[]> GenerateFingerprintAsync(
        string filePath,
        int sampleRate,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var tempFile = Path.Join(
            Path.GetTempPath(),
            "jellyfin-segrec-" + Guid.NewGuid().ToString("N") + ".chromaprint");

        try
        {
            var args = new List<string>
            {
                "-nostdin",
                "-hide_banner",
                "-y",
                "-ss", startSeconds.ToString(CultureInfo.InvariantCulture),
                "-i", filePath,
                "-ac", "1",
                "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                "-t", durationSeconds.ToString(CultureInfo.InvariantCulture),
                "-vn", "-sn", "-dn",
                "-f", "chromaprint",
                "-fp_format", "raw",
                tempFile,
            };

            _logger.LogDebug("Running ffmpeg chromaprint: {Args}", string.Join(' ', args));

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.EncoderPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            foreach (var a in args)
            {
                process.StartInfo.ArgumentList.Add(a);
            }

            process.Start();

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Win32Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set ffmpeg process priority to BelowNormal");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to set ffmpeg process priority to BelowNormal");
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "Failed to set ffmpeg process priority to BelowNormal");
            }

            try
            {
                // Drain both stdout and stderr concurrently to prevent pipe buffer deadlocks.
                // ffmpeg writes progress/diagnostics to stderr; chromaprint output goes to the
                // temp file, but stdout may still receive data depending on the muxer.
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

                await Task.WhenAll(stderrTask, stdoutTask).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg chromaprint failed with exit code {process.ExitCode} for {filePath}");
            }

            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
            {
                _logger.LogDebug("Chromaprint output file is empty for {File}", filePath);
                return [];
            }

            return await File.ReadAllBytesAsync(tempFile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed to delete temp file {TempFile}", tempFile);
            }
        }
    }

    /// <summary>
    /// Probes the audio stream's actual duration using ffprobe.
    /// Containers can report a duration based on the longest stream (e.g. MKV with subtitles
    /// that extend beyond the audio/video). This method reads the per-stream duration to get
    /// the real audio length. Supports MP4 (stream duration field), MKV (DURATION tag), and
    /// other container formats.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audio duration in seconds, or <c>null</c> if it could not be determined.</returns>
    public async Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken)
    {
        var probePath = ResolveProbePath(_mediaEncoder.EncoderPath, File.Exists);
        if (probePath is null)
        {
            _logger.LogDebug("ffprobe not found next to {EncoderPath}, cannot probe audio duration", _mediaEncoder.EncoderPath);
            return null;
        }

        // Query both stream-level duration (MP4/MOV/AVI) and stream tags (MKV DURATION tag)
        // in a single call. One or both may return "N/A" depending on the container format.
        var args = new List<string>
        {
            "-v", "error",
            "-select_streams", "a:0",
            "-show_entries", "stream=duration:stream_tags=DURATION",
            "-of", "csv=p=0",
            filePath,
        };

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = probePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var a in args)
        {
            process.StartInfo.ArgumentList.Add(a);
        }

        process.Start();

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            // Output is CSV: "duration,DURATION_tag" e.g. "1481.088000,N/A" or "N/A,00:24:41.088000000"
            var output = (await stdoutTask.ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            // Try each comma-separated field - the first valid one wins
            var fields = output.Split('\n')[0].Trim().Split(',');
            foreach (var value in fields.Select(f => f.Trim()))
            {
                if (string.IsNullOrEmpty(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Plain seconds (MP4 stream duration: "1481.088000")
                if (double.TryParse(value, CultureInfo.InvariantCulture, out var secs) && secs > 0)
                {
                    return secs;
                }

                // HH:MM:SS.nnnnnnnnn (MKV DURATION tag: "00:24:41.088000000")
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts) && ts.TotalSeconds > 0)
                {
                    return ts.TotalSeconds;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EnsureProcessKilled(process);
            _logger.LogDebug(ex, "Failed to probe audio duration for {File}", filePath);
            return null;
        }
        catch (OperationCanceledException)
        {
            EnsureProcessKilled(process);
            throw;
        }
    }

    /// <summary>
    /// Derives the path to <c>ffprobe</c> from the configured <c>ffmpeg</c> path.
    /// </summary>
    /// <remarks>
    /// Only the <em>file name</em> is rewritten. A whole-path string replace mangles the
    /// directory on the standard Jellyfin layouts - <c>/usr/lib/jellyfin-ffmpeg/ffmpeg</c> became
    /// <c>/usr/lib/jellyfin-ffprobe/ffprobe</c>, which does not exist, silently disabling audio
    /// duration probing for most Linux and Docker installs. The executable extension is preserved
    /// so <c>ffmpeg.exe</c> maps to <c>ffprobe.exe</c>.
    /// </remarks>
    /// <param name="encoderPath">The configured ffmpeg executable path.</param>
    /// <param name="exists">Existence predicate (injected for testability).</param>
    /// <returns>The ffprobe path, or <c>null</c> when no candidate exists.</returns>
    internal static string? ResolveProbePath(string? encoderPath, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (string.IsNullOrEmpty(encoderPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(encoderPath);
        var fileName = Path.GetFileName(encoderPath);
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        // "ffmpeg" -> "ffprobe", and vendored names like "jellyfin-ffmpeg" -> "jellyfin-ffprobe".
        // Replace only the last occurrence so a directory-like prefix in the stem is preserved.
        var probeStem = stem.LastIndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) is var idx and >= 0
            ? string.Concat(stem.AsSpan(0, idx), "ffprobe", stem.AsSpan(idx + "ffmpeg".Length))
            : stem + "probe";

        var candidates = new[]
        {
            string.IsNullOrEmpty(directory) ? probeStem + extension : Path.Join(directory, probeStem + extension),
            string.IsNullOrEmpty(directory) ? "ffprobe" + extension : Path.Join(directory, "ffprobe" + extension),
        };

        foreach (var candidate in candidates)
        {
            if (exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Kills the process and its entire process tree if it hasn't already exited.
    /// Process.Dispose() does NOT kill the child process - it must be done explicitly.
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
            // Process already exited between the check and the kill - safe to ignore.
        }
    }
}
