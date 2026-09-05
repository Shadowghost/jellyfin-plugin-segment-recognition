using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Covers which ffmpeg the plugin runs.
/// </summary>
/// <remarks>
/// <see cref="IMediaEncoder.EncoderPath"/> is only set once Jellyfin's own validation has passed,
/// so an empty value is not the same as "no ffmpeg". Treating it that way skipped analysis on
/// installs where ffmpeg is on <c>$PATH</c> or validation was turned off.
/// </remarks>
public sealed class FfmpegPathResolutionTests
{
    [Fact]
    public void ValidatedPathWins()
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns("/usr/lib/jellyfin-ffmpeg/ffmpeg");

        Assert.Equal(
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            FfmpegPathResolver.Resolve(encoder, WithConfiguredPath("/opt/ffmpeg/ffmpeg")));
    }

    /// <summary>
    /// The configured path is what Jellyfin would have validated, so it is the next best thing.
    /// </summary>
    [Fact]
    public void ConfiguredPathIsUsedWhenNothingWasValidated()
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(string.Empty);

        Assert.Equal(
            "/opt/ffmpeg/ffmpeg",
            FfmpegPathResolver.Resolve(encoder, WithConfiguredPath("/opt/ffmpeg/ffmpeg")));
    }

    /// <summary>
    /// Jellyfin's own last resort, and the case that used to disable analysis outright.
    /// </summary>
    [Fact]
    public void FallsBackToTheExecutableOnPath()
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(string.Empty);

        Assert.Equal("ffmpeg", FfmpegPathResolver.Resolve(encoder, WithConfiguredPath(string.Empty)));
    }

    /// <summary>
    /// A configuration manager that has no encoding options yet must not throw on the way past.
    /// </summary>
    [Fact]
    public void MissingEncodingOptionsFallBackToThePathExecutable()
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns((string?)null);

        Assert.Equal(
            "ffmpeg",
            FfmpegPathResolver.Resolve(encoder, Substitute.For<IConfigurationManager>()));
    }

    private static IConfigurationManager WithConfiguredPath(string encoderAppPath)
    {
        var configurationManager = Substitute.For<IConfigurationManager>();
        configurationManager.GetConfiguration("encoding").Returns(new EncodingOptions { EncoderAppPath = encoderAppPath });
        return configurationManager;
    }
}
