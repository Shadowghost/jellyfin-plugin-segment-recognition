using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Probes the installed ffmpeg for the muxers and filters the analysis pipelines depend on, and
/// reports what is missing once at startup.
/// </summary>
/// <remarks>
/// Without it an ffmpeg built without chromaprint surfaces as one fingerprinting exception per
/// episode, hours into a scheduled run, with nothing in the log that names the actual cause.
/// </remarks>
public sealed class FfmpegCapabilityService : IHostedService, IDisposable
{
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegCapabilityService> _logger;
    private FfmpegCapabilities? _probed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegCapabilityService"/> class.
    /// </summary>
    /// <param name="mediaEncoder">The media encoder, for the ffmpeg path Jellyfin is configured with.</param>
    /// <param name="logger">The logger.</param>
    public FfmpegCapabilityService(IMediaEncoder mediaEncoder, ILogger<FfmpegCapabilityService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets what the probe found, or <see cref="FfmpegCapabilities.Unknown"/> while it has not
    /// produced an answer yet.
    /// </summary>
    /// <remarks>
    /// Callers that can await should use <see cref="GetAsync"/>, which runs the probe if it has
    /// not run yet.
    /// </remarks>
    public FfmpegCapabilities Capabilities => _probed ?? FfmpegCapabilities.Unknown;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => GetAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _probeLock.Dispose();

    /// <summary>
    /// Returns the probe result, running the probe the first time it is asked for.
    /// </summary>
    /// <remarks>
    /// A probe that could not reach ffmpeg is not remembered. The startup check runs alongside the
    /// rest of the server's own startup, so the encoder path can still be unset when it fires;
    /// caching that non-answer would leave every later caller with it.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detected capabilities.</returns>
    public async Task<FfmpegCapabilities> GetAsync(CancellationToken cancellationToken)
    {
        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A probe that could not reach ffmpeg leaves _probed unset so the next caller retries,
            // and reports Unknown in the meantime - never less than what ffmpeg can really do.
            _probed ??= await ProbeAsync(cancellationToken).ConfigureAwait(false);
            return Capabilities;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <summary>
    /// Runs the probe and logs what it found.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detected capabilities, or <see langword="null"/> when ffmpeg could not be reached.</returns>
    private async Task<FfmpegCapabilities?> ProbeAsync(CancellationToken cancellationToken)
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoderPath))
        {
            _logger.LogWarning("No ffmpeg path is configured in Jellyfin; skipping the ffmpeg capability check");
            return null;
        }

        try
        {
            var version = await RunAsync(encoderPath, ["-hide_banner", "-version"], cancellationToken).ConfigureAwait(false);
            var muxers = await RunAsync(encoderPath, ["-hide_banner", "-muxers"], cancellationToken).ConfigureAwait(false);

            var chromaprint = muxers.Contains("chromaprint", StringComparison.OrdinalIgnoreCase);

            // Only meaningful when the muxer exists: asking for help on a missing muxer prints
            // nothing to match against, which would report the raw format as missing too.
            var rawFingerprints = chromaprint
                && (await RunAsync(encoderPath, ["-hide_banner", "-h", "muxer=chromaprint"], cancellationToken)
                    .ConfigureAwait(false))
                    .Contains("binary raw fingerprint", StringComparison.OrdinalIgnoreCase);

            var silenceDetect = (await RunAsync(encoderPath, ["-hide_banner", "-h", "filter=silencedetect"], cancellationToken)
                    .ConfigureAwait(false))
                .Contains("noise tolerance", StringComparison.OrdinalIgnoreCase);

            var blackFrame = (await RunAsync(encoderPath, ["-hide_banner", "-h", "filter=blackframe"], cancellationToken)
                    .ConfigureAwait(false))
                .Contains("percentage of the pixels", StringComparison.OrdinalIgnoreCase);

            var capabilities = new FfmpegCapabilities(chromaprint, rawFingerprints, silenceDetect, blackFrame);
            Report(capabilities, FirstLine(version), encoderPath);
            return capabilities;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not probe ffmpeg at {Path}; assuming it supports everything", encoderPath);
            return null;
        }
    }

    private static string FirstLine(string output)
    {
        var newline = output.IndexOf('\n', StringComparison.Ordinal);
        return (newline < 0 ? output : output[..newline]).Trim();
    }

    private static async Task<string> RunAsync(string encoderPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = encoderPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        // ffmpeg writes -h output to stdout and diagnostics to stderr; both are drained
        // concurrently so neither pipe can fill up and deadlock the process.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_probeTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillQuietly(process);
            throw new TimeoutException($"ffmpeg did not answer {string.Join(' ', arguments)} within {_probeTimeout.TotalSeconds:F0}s");
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        return await stdoutTask.ConfigureAwait(false) + await stderrTask.ConfigureAwait(false);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The process is already gone, which is the outcome we wanted.
        }
    }

    private void Report(FfmpegCapabilities capabilities, string version, string encoderPath)
    {
        if (!capabilities.Chromaprint)
        {
            _logger.LogWarning(
                "ffmpeg at {Path} has no chromaprint muxer - audio fingerprinting cannot run. Jellyfin's bundled ffmpeg includes it; a distribution build may not. ({Version})",
                encoderPath,
                version);
        }
        else if (!capabilities.RawFingerprints)
        {
            _logger.LogWarning(
                "ffmpeg at {Path} has a chromaprint muxer that does not understand -fp_format raw - audio fingerprinting cannot run. ({Version})",
                encoderPath,
                version);
        }

        if (!capabilities.SilenceDetect)
        {
            _logger.LogWarning(
                "ffmpeg at {Path} has no silencedetect filter - segment boundaries will not be snapped to silence. ({Version})",
                encoderPath,
                version);
        }

        if (!capabilities.BlackFrame)
        {
            _logger.LogWarning(
                "ffmpeg at {Path} has no blackframe filter - black frame analysis cannot run. ({Version})",
                encoderPath,
                version);
        }

        if (capabilities.CanFingerprint && capabilities.SilenceDetect && capabilities.BlackFrame)
        {
            _logger.LogInformation("ffmpeg supports everything the analysis pipelines need ({Version})", version);
        }
    }
}
