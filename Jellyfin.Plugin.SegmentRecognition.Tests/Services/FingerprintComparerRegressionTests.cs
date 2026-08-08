using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Regression tests for the fingerprint comparer's indexing and run-length accounting.
/// </summary>
public sealed class FingerprintComparerRegressionTests
{
    private const double SecondsPerPoint = 0.1238;

    private static byte[] ToBytes(IEnumerable<uint> points)
        => points.SelectMany(BitConverter.GetBytes).ToArray();

    private static uint[] Noise(int count, int seed)
    {
        // Deterministic pseudo-random points; a fixed seed keeps the test reproducible.
        var rng = new Random(seed);
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (uint)rng.Next(int.MinValue, int.MaxValue);
        }

        return values;
    }

    /// <summary>
    /// The index used to record only the FIRST position of each distinct point value, so an
    /// alignment that depended on a later repeat was discarded before the Hamming scan ever ran.
    /// Here the shared run only aligns against B's second copy.
    /// </summary>
    [Fact]
    public void MatchIsFoundWhenItAlignsToALaterRepeatOfAPoint()
    {
        var shared = Noise(300, seed: 1);

        // A: [shared]
        var a = shared;

        // B: [shared-with-different-values-first][shared]. The leading block repeats B's own
        // points so the naive "first occurrence" index points into the wrong copy.
        var b = shared.Concat(shared).ToArray();

        var regions = FingerprintComparer.FindMatchedRegions(
            ToBytes(a),
            ToBytes(b),
            maxBitErrors: 0,
            maxTimeSkipSeconds: 3.5,
            invertedIndexShift: 0,
            minMatchDurationSeconds: 10,
            CancellationToken.None);

        Assert.NotEmpty(regions);
    }

    /// <summary>
    /// A run of exactly the minimum length must qualify. Counting the run as
    /// <c>end - start</c> made it one point short of the threshold.
    /// </summary>
    [Fact]
    public void RunOfExactlyMinimumLength_Qualifies()
    {
        const int minSeconds = 10;
        var exactPoints = (int)(minSeconds / SecondsPerPoint);

        var shared = Noise(exactPoints, seed: 7);
        var a = shared.Concat(Noise(50, seed: 8)).ToArray();
        var b = shared.Concat(Noise(50, seed: 9)).ToArray();

        var regions = FingerprintComparer.FindMatchedRegions(
            ToBytes(a),
            ToBytes(b),
            maxBitErrors: 0,
            maxTimeSkipSeconds: 3.5,
            invertedIndexShift: 0,
            minMatchDurationSeconds: minSeconds,
            CancellationToken.None);

        Assert.NotEmpty(regions);
    }

    [Fact]
    public void IdenticalFingerprints_MatchTheWholeSpan()
    {
        var points = Noise(500, seed: 3);
        var bytes = ToBytes(points);

        var regions = FingerprintComparer.FindMatchedRegions(
            bytes, bytes,
            maxBitErrors: 0,
            maxTimeSkipSeconds: 3.5,
            invertedIndexShift: 0,
            minMatchDurationSeconds: 10,
            CancellationToken.None);

        var longest = regions[0];
        var durationSeconds = (longest.EndTicks - longest.StartTicks) / (double)TimeSpan.TicksPerSecond;
        Assert.InRange(durationSeconds, 500 * SecondsPerPoint * 0.95, 500 * SecondsPerPoint * 1.05);
    }

    [Fact]
    public void UnrelatedFingerprints_ProduceNoRegions()
    {
        var regions = FingerprintComparer.FindMatchedRegions(
            ToBytes(Noise(600, seed: 11)),
            ToBytes(Noise(600, seed: 22)),
            maxBitErrors: 0,
            maxTimeSkipSeconds: 3.5,
            invertedIndexShift: 2,
            minMatchDurationSeconds: 10,
            CancellationToken.None);

        Assert.Empty(regions);
    }

    /// <summary>
    /// A long constant stretch (digital silence) must not blow up: the index caps how many
    /// positions it keeps per distinct value, so votes stay linear rather than quadratic.
    /// </summary>
    [Fact]
    public void LongConstantRun_CompletesQuickly()
    {
        var constant = Enumerable.Repeat(0u, 4000).ToArray();

        var regions = FingerprintComparer.FindMatchedRegions(
            ToBytes(constant),
            ToBytes(constant),
            maxBitErrors: 0,
            maxTimeSkipSeconds: 3.5,
            invertedIndexShift: 2,
            minMatchDurationSeconds: 10,
            CancellationToken.None);

        Assert.NotEmpty(regions);
    }

    [Fact]
    public void EmptyInput_ReturnsNoRegions()
    {
        Assert.Empty(FingerprintComparer.FindMatchedRegions(
            [], ToBytes(Noise(100, 1)), 0, 3.5, 0, 10, CancellationToken.None));

        Assert.Empty(FingerprintComparer.FindMatchedRegions(
            ToBytes(Noise(100, 1)), [], 0, 3.5, 0, 10, CancellationToken.None));
    }

    [Fact]
    public void RegionsAreOrderedLongestFirst()
    {
        var shared = Noise(400, seed: 31);
        var a = Noise(60, 41).Concat(shared).ToArray();
        var b = Noise(20, 51).Concat(shared).ToArray();

        var regions = FingerprintComparer.FindMatchedRegions(
            ToBytes(a), ToBytes(b),
            maxBitErrors: 0,
            maxTimeSkipSeconds: 3.5,
            invertedIndexShift: 0,
            minMatchDurationSeconds: 5,
            CancellationToken.None);

        var lengths = regions.Select(r => r.EndTicks - r.StartTicks).ToList();
        Assert.Equal(lengths.OrderByDescending(l => l).ToList(), lengths);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => FingerprintComparer.FindMatchedRegions(
            ToBytes(Noise(2000, 61)),
            ToBytes(Noise(2000, 62)),
            maxBitErrors: 6,
            maxTimeSkipSeconds: 3.5,
            invertedIndexShift: 2,
            minMatchDurationSeconds: 10,
            cts.Token));
    }
}
