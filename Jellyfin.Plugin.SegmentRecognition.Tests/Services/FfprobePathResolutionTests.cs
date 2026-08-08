using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Regression tests for deriving the ffprobe path from the configured ffmpeg path.
/// </summary>
/// <remarks>
/// The original implementation did a whole-path string replace, which mangled the directory on
/// exactly the layout Jellyfin ships on Linux and in Docker
/// (<c>/usr/lib/jellyfin-ffmpeg/ffmpeg</c> became <c>/usr/lib/jellyfin-ffprobe/ffprobe</c>).
/// The path never existed, so audio-duration probing silently did nothing for most installs even
/// though it is enabled by default.
/// </remarks>
public sealed class FfprobePathResolutionTests
{
    private static Func<string, bool> Exists(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.Ordinal);
        return p => set.Contains(p);
    }

    [Fact]
    public void JellyfinLinuxLayout_ResolvesSiblingFfprobe()
    {
        var result = FfmpegChromaprintService.ResolveProbePath(
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            Exists("/usr/lib/jellyfin-ffmpeg/ffprobe"));

        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffprobe", result);
    }

    [Fact]
    public void PlainUsrBinLayout_ResolvesSiblingFfprobe()
    {
        var result = FfmpegChromaprintService.ResolveProbePath(
            "/usr/bin/ffmpeg",
            Exists("/usr/bin/ffprobe"));

        Assert.Equal("/usr/bin/ffprobe", result);
    }

    [Fact]
    public void WindowsLayout_PreservesExecutableExtension()
    {
        var result = FfmpegChromaprintService.ResolveProbePath(
            @"C:\Program Files\Jellyfin\Server\ffmpeg.exe",
            Exists(@"C:\Program Files\Jellyfin\Server\ffprobe.exe"));

        Assert.Equal(@"C:\Program Files\Jellyfin\Server\ffprobe.exe", result);
    }

    /// <summary>
    /// A vendored binary name keeps its prefix: only the "ffmpeg" token is rewritten.
    /// </summary>
    [Fact]
    public void VendoredBinaryName_RewritesOnlyTheFfmpegToken()
    {
        var result = FfmpegChromaprintService.ResolveProbePath(
            "/opt/tools/jellyfin-ffmpeg",
            Exists("/opt/tools/jellyfin-ffprobe"));

        Assert.Equal("/opt/tools/jellyfin-ffprobe", result);
    }

    /// <summary>
    /// When the vendored name has no sibling, fall back to a plain "ffprobe" in the same directory.
    /// </summary>
    [Fact]
    public void FallsBackToPlainFfprobeInSameDirectory()
    {
        var result = FfmpegChromaprintService.ResolveProbePath(
            "/opt/tools/jellyfin-ffmpeg",
            Exists("/opt/tools/ffprobe"));

        Assert.Equal("/opt/tools/ffprobe", result);
    }

    /// <summary>
    /// The directory must never be rewritten - this is the exact bug the old implementation had.
    /// </summary>
    [Fact]
    public void NeverRewritesTheDirectoryComponent()
    {
        var probed = new List<string>();
        FfmpegChromaprintService.ResolveProbePath(
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            p =>
            {
                probed.Add(p);
                return false;
            });

        Assert.All(probed, p => Assert.StartsWith("/usr/lib/jellyfin-ffmpeg/", p, StringComparison.Ordinal));
        Assert.DoesNotContain(probed, p => p.Contains("jellyfin-ffprobe/", StringComparison.Ordinal));
    }

    [Fact]
    public void ReturnsNullWhenNothingExists()
    {
        Assert.Null(FfmpegChromaprintService.ResolveProbePath("/usr/bin/ffmpeg", Exists()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ReturnsNullForMissingEncoderPath(string? encoderPath)
    {
        Assert.Null(FfmpegChromaprintService.ResolveProbePath(encoderPath, _ => true));
    }
}
