using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests;

/// <summary>
/// Time range tests.
/// </summary>
public class TestTimeRanges
{
    /// <summary>
    /// Tests small range.
    /// </summary>
    [Fact]
    public void TestSmallRange()
    {
        var times = new double[]{
            1, 1.5, 2, 2.5, 3, 3.5, 4,
            100, 100.5, 101, 101.5
        };

        var expected = new TimeRange(1, 4);
        var actual = TimeRangeHelpers.FindContiguous(times, 2);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Tests large range.
    /// </summary>
    [Fact]
    public void TestLargeRange()
    {
        var times = new double[]{
            1, 1.5, 2,
            2.8, 2.9, 2.995, 3.0, 3.01, 3.02, 3.4, 3.45, 3.48, 3.7, 3.77, 3.78, 3.781, 3.782, 3.789, 3.85,
            4.5, 5.3122, 5.3123, 5.3124, 5.3125, 5.3126, 5.3127, 5.3128,
            55, 55.5, 55.6, 55.7
        };

        var expected = new TimeRange(1, 5.3128);
        var actual = TimeRangeHelpers.FindContiguous(times, 2);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Tests Futurama example range.
    /// </summary>
    [Fact]
    public void TestFuturama()
    {
        // These timestamps were manually extracted from Futurama S01E04 and S01E05.
        var times = new double[]{
            2.176, 8.32, 10.112, 11.264, 13.696, 16, 16.128, 16.64, 16.768, 16.896, 17.024, 17.152, 17.28,
            17.408, 17.536, 17.664, 17.792, 17.92, 18.048, 18.176, 18.304, 18.432, 18.56, 18.688, 18.816,
            18.944, 19.072, 19.2, 19.328, 19.456, 19.584, 19.712, 19.84, 19.968, 20.096, 20.224, 20.352,
            20.48, 20.608, 20.736, 20.864, 20.992, 21.12, 21.248, 21.376, 21.504, 21.632, 21.76, 21.888,
            22.016, 22.144, 22.272, 22.4, 22.528, 22.656, 22.784, 22.912, 23.04, 23.168, 23.296, 23.424,
            23.552, 23.68, 23.808, 23.936, 24.064, 24.192, 24.32, 24.448, 24.576, 24.704, 24.832, 24.96,
            25.088, 25.216, 25.344, 25.472, 25.6, 25.728, 25.856, 25.984, 26.112, 26.24, 26.368, 26.496,
            26.624, 26.752, 26.88, 27.008, 27.136, 27.264, 27.392, 27.52, 27.648, 27.776, 27.904, 28.032,
            28.16, 28.288, 28.416, 28.544, 28.672, 28.8, 28.928, 29.056, 29.184, 29.312, 29.44, 29.568,
            29.696, 29.824, 29.952, 30.08, 30.208, 30.336, 30.464, 30.592, 30.72, 30.848, 30.976, 31.104,
            31.232, 31.36, 31.488, 31.616, 31.744, 31.872, 32, 32.128, 32.256, 32.384, 32.512, 32.64,
            32.768, 32.896, 33.024, 33.152, 33.28, 33.408, 33.536, 33.664, 33.792, 33.92, 34.048, 34.176,
            34.304, 34.432, 34.56, 34.688, 34.816, 34.944, 35.072, 35.2, 35.328, 35.456, 35.584, 35.712,
            35.84, 35.968, 36.096, 36.224, 36.352, 36.48, 36.608, 36.736, 36.864, 36.992, 37.12, 37.248,
            37.376, 37.504, 37.632, 37.76, 37.888, 38.016, 38.144, 38.272, 38.4, 38.528, 38.656, 38.784,
            38.912, 39.04, 39.168, 39.296, 39.424, 39.552, 39.68, 39.808, 39.936, 40.064, 40.192, 40.32,
            40.448, 40.576, 40.704, 40.832, 40.96, 41.088, 41.216, 41.344, 41.472, 41.6, 41.728, 41.856,
            41.984, 42.112, 42.24, 42.368, 42.496, 42.624, 42.752, 42.88, 43.008, 43.136, 43.264, 43.392,
            43.52, 43.648, 43.776, 43.904, 44.032, 44.16, 44.288, 44.416, 44.544, 44.672, 44.8, 44.928,
            45.056, 45.184, 57.344, 62.976, 68.864, 74.368, 81.92, 82.048, 86.528, 100.864, 102.656,
            102.784, 102.912, 103.808, 110.976, 116.864, 125.696, 128.384, 133.248, 133.376, 136.064,
            136.704, 142.976, 150.272, 152.064, 164.864, 164.992, 166.144, 166.272, 175.488, 190.08,
            191.872, 192, 193.28, 193.536, 213.376, 213.504, 225.664, 225.792, 243.2, 243.84, 256,
            264.448, 264.576, 264.704, 269.568, 274.816, 274.944, 276.096, 283.264, 294.784, 294.912,
            295.04, 295.168, 313.984, 325.504, 333.568, 335.872, 336.384
        };

        var expected = new TimeRange(16, 45.184);
        var actual = TimeRangeHelpers.FindContiguous(times, 2);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Tests that TimeRange intersections are detected correctly.
    /// Tests each time range against a range of 5 to 10 seconds.
    /// </summary>
    [Theory]
    [InlineData(1, 4, false)]   // too early
    [InlineData(4, 6, true)]    // intersects on the left
    [InlineData(7, 8, true)]    // in the middle
    [InlineData(9, 12, true)]   // intersects on the right
    [InlineData(13, 15, false)] // too late
    public void TestTimeRangeIntersection(int start, int end, bool expected)
    {
        var large = new TimeRange(5, 10);
        var testRange = new TimeRange(start, end);

        Assert.Equal(expected, large.Intersects(testRange));
    }
}
