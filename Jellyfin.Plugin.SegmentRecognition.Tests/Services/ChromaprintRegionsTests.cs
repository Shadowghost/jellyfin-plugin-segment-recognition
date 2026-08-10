using Jellyfin.Plugin.SegmentRecognition.Services;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Tests for how much of an item is fingerprinted for intro matching.
/// </summary>
public sealed class ChromaprintRegionsTests
{
    [Theory]
    [InlineData(300, 300)]      // short media: the whole item
    [InlineData(600, 600)]      // still short media at the boundary
    [InlineData(1440, 360)]     // 24 min anime: the fraction
    [InlineData(2400, 600)]     // 40 min: fraction and ceiling coincide
    [InlineData(3624, 600)]     // 60 min drama: the ceiling binds
    public void FirstPass_TakesTheSmallerOfFractionAndCeiling(double runtime, double expected)
        => Assert.Equal(expected, ChromaprintRegions.FirstPass(runtime), 3);

    /// <summary>
    /// The retry width is half the runtime: the matcher rejects an intro starting past the
    /// midpoint, so anything beyond that could never be accepted anyway.
    /// </summary>
    [Fact]
    public void ForRetry_IsHalfTheRuntime()
    {
        Assert.Equal(1812, ChromaprintRegions.ForRetry(3624), 3);
        Assert.Equal(720, ChromaprintRegions.ForRetry(1440), 3);
    }

    /// <summary>
    /// The retry only ever widens. If it could narrow, a retry would discard a fingerprint that
    /// already covered more than the first pass asked for.
    /// </summary>
    [Theory]
    [InlineData(1440)]
    [InlineData(2400)]
    [InlineData(3624)]
    [InlineData(5400)]
    public void ForRetry_IsNeverNarrowerThanFirstPass(double runtime)
        => Assert.True(ChromaprintRegions.ForRetry(runtime) >= ChromaprintRegions.FirstPass(runtime));
}
