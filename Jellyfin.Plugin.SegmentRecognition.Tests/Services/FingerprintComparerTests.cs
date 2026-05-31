using System;
using System.Runtime.InteropServices;
using System.Threading;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

public class FingerprintComparerTests
{
    private const int DefaultMaxBitErrors = 6;
    private const double DefaultMaxTimeSkipSeconds = 3.5;
    private const int DefaultInvertedIndexShift = 2;
    private const int DefaultMinMatchDurationSeconds = 15;

    /// <summary>
    /// Two identical fingerprints should produce a matched region.
    /// </summary>
    [Fact]
    public void IdenticalFingerprints_ReturnsMatchedRegion()
    {
        // ~20 seconds of fingerprint data (162 uint points * 0.1238s ≈ 20s)
        var fingerprint = CreateFingerprint(162, seed: 42);

        var results = FingerprintComparer.FindMatchedRegions(
            fingerprint,
            fingerprint,
            DefaultMaxBitErrors,
            DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift,
            DefaultMinMatchDurationSeconds,
            CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].StartTicks >= 0);
        Assert.True(results[0].EndTicks > results[0].StartTicks);
    }

    /// <summary>
    /// Empty fingerprints should return no matches.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EmptyFingerprints_ReturnsEmpty(bool aEmpty, bool bEmpty)
    {
        var a = aEmpty ? [] : CreateFingerprint(200, seed: 1);
        var b = bEmpty ? [] : CreateFingerprint(200, seed: 2);

        var results = FingerprintComparer.FindMatchedRegions(
            a, b, DefaultMaxBitErrors, DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift, DefaultMinMatchDurationSeconds, CancellationToken.None);

        Assert.Empty(results);
    }

    /// <summary>
    /// Completely different fingerprints should return no matches.
    /// </summary>
    [Fact]
    public void CompletelyDifferentFingerprints_ReturnsEmpty()
    {
        var a = CreateFingerprint(200, seed: 1);
        var b = CreateFingerprint(200, seed: 999);

        var results = FingerprintComparer.FindMatchedRegions(
            a, b, DefaultMaxBitErrors, DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift, DefaultMinMatchDurationSeconds, CancellationToken.None);

        Assert.Empty(results);
    }

    /// <summary>
    /// A match shorter than the minimum duration should be filtered out.
    /// </summary>
    [Fact]
    public void ShortMatch_BelowMinDuration_ReturnsEmpty()
    {
        // ~5 seconds of identical data (40 points * 0.1238s ≈ 5s)
        var fingerprint = CreateFingerprint(40, seed: 42);

        var results = FingerprintComparer.FindMatchedRegions(
            fingerprint,
            fingerprint,
            DefaultMaxBitErrors,
            DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift,
            minMatchDurationSeconds: 10,
            CancellationToken.None);

        Assert.Empty(results);
    }

    /// <summary>
    /// Two fingerprints with a shared prefix should detect the matching region.
    /// </summary>
    [Fact]
    public void SharedPrefix_DetectsMatch()
    {
        // 200 points shared, then diverge
        var sharedCount = 200;
        var totalCount = 400;
        var a = CreateFingerprint(totalCount, seed: 42);
        var b = CreateFingerprint(totalCount, seed: 99);

        // Copy the shared prefix from a to b
        Buffer.BlockCopy(a, 0, b, 0, sharedCount * sizeof(uint));

        var results = FingerprintComparer.FindMatchedRegions(
            a, b, DefaultMaxBitErrors, DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift, DefaultMinMatchDurationSeconds, CancellationToken.None);

        Assert.Single(results);
        // The match should start near tick 0
        Assert.True(results[0].StartTicks < TimeSpan.TicksPerSecond * 2);
    }

    /// <summary>
    /// Two shared regions separated by a divergent gap should both be returned, sorted
    /// longest-first. This protects callers (e.g. the Chromaprint intro/outro window filter)
    /// from being forced onto a single-longest match that overshoots their duration window.
    /// </summary>
    [Fact]
    public void TwoSeparateSharedRegions_ReturnsBothRegions_LongestFirst()
    {
        const int totalPoints = 800;
        const int shortStart = 40;
        const int shortLen = 80;   // ~10 s (above the 5 s min in the request below)
        const int longStart = 400;
        const int longLen = 200;   // ~25 s

        var a = CreateFingerprint(totalPoints, seed: 42);
        var b = CreateFingerprint(totalPoints, seed: 99);

        // Make both fingerprints agree on two disjoint runs, separated by a large divergent
        // gap so they can't be merged into one contiguous run.
        Buffer.BlockCopy(a, shortStart * sizeof(uint), b, shortStart * sizeof(uint), shortLen * sizeof(uint));
        Buffer.BlockCopy(a, longStart * sizeof(uint), b, longStart * sizeof(uint), longLen * sizeof(uint));

        var results = FingerprintComparer.FindMatchedRegions(
            a,
            b,
            DefaultMaxBitErrors,
            DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift,
            minMatchDurationSeconds: 5,
            CancellationToken.None);

        Assert.True(results.Count >= 2, $"expected at least 2 regions, got {results.Count}");

        // Longest first: region[0] length >= region[1] length.
        for (int i = 1; i < results.Count; i++)
        {
            var prev = results[i - 1].EndTicks - results[i - 1].StartTicks;
            var cur = results[i].EndTicks - results[i].StartTicks;
            Assert.True(prev >= cur, "regions not sorted descending by length");
        }
    }

    /// <summary>
    /// Cancellation should be respected.
    /// </summary>
    [Fact]
    public void CancelledToken_ThrowsOperationCancelledException()
    {
        var fingerprint = CreateFingerprint(200, seed: 42);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            FingerprintComparer.FindMatchedRegions(
                fingerprint, fingerprint,
                DefaultMaxBitErrors, DefaultMaxTimeSkipSeconds,
                DefaultInvertedIndexShift, DefaultMinMatchDurationSeconds, cts.Token));
    }

    /// <summary>
    /// With maxBitErrors=0, only exact matches should be found.
    /// Introducing single-bit differences should reduce the match.
    /// </summary>
    [Fact]
    public void StrictBitErrors_RequiresExactMatch()
    {
        var a = CreateFingerprint(200, seed: 42);
        var b = (byte[])a.Clone();

        // Flip one bit in every other uint in b
        var uintsB = MemoryMarshal.Cast<byte, uint>(b.AsSpan());
        for (int i = 0; i < uintsB.Length; i += 2)
        {
            uintsB[i] ^= 1;
        }

        // With 0 bit errors, the flipped points won't match
        var strictResults = FingerprintComparer.FindMatchedRegions(
            a, b, maxBitErrors: 0, DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift, DefaultMinMatchDurationSeconds, CancellationToken.None);

        // With 6 bit errors, they should still match
        var relaxedResults = FingerprintComparer.FindMatchedRegions(
            a, b, maxBitErrors: 6, DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift, DefaultMinMatchDurationSeconds, CancellationToken.None);

        Assert.Single(relaxedResults);
        // Strict may or may not find a match depending on gap tolerance, but it should be shorter or empty
        if (strictResults.Count > 0)
        {
            Assert.True(
                relaxedResults[0].EndTicks - relaxedResults[0].StartTicks
                >= strictResults[0].EndTicks - strictResults[0].StartTicks);
        }
    }

    /// <summary>
    /// Regression test for a real intro that matches well within <c>maxBitErrors</c>
    /// but shares almost no <em>exact</em> 32-bit points (noisy surround→mono downmix)
    /// and sits at a non-zero alignment shift (variable-length cold open).
    /// The inverted index only votes on near-exact matches, so the correct shift collects
    /// just a handful of votes. The old <c>hitCount &lt; minMatchPoints / 4</c> gate discarded it
    /// before the Hamming scan ever ran, leaving the whole series with no segments. The match must
    /// now survive the (low) vote gate and be verified by the point-by-point scan.
    /// </summary>
    [Fact]
    public void RealMatchWithFewExactVotes_AtShift_IsStillFound()
    {
        var rng = new Random(7);

        // Shared 200-point (~24.8 s) intro region, well above the 15 s minimum.
        const int sharedPoints = 200;
        var shared = new uint[sharedPoints];
        for (int i = 0; i < sharedPoints; i++)
        {
            shared[i] = (uint)rng.Next();
        }

        // Different-length preambles create a non-zero alignment shift, mimicking cold opens of
        // different lengths in front of the same title sequence.
        var a = ConcatPoints(RandomPoints(rng, 60), shared, RandomPoints(rng, 80));

        // In B the shared region differs by a single flipped low bit per point (≤ maxBitErrors=6)
        // so it still matches the Hamming scan, but is never byte-identical and never equals a ±2-bit
        // rotation of the A point - so the inverted index casts no vote for it. A handful of points
        // are left identical as alignment seeds: enough that the correct shift clears any sensibly
        // low vote gate, but far below the old gate of minMatchPoints/4 (≈30) that discarded the
        // match entirely. Each seed contributes exactly one vote to the (single) correct shift.
        var seedIndices = new[] { 20, 50, 80, 110, 140, 170 };
        var perturbed = new uint[sharedPoints];
        for (int i = 0; i < sharedPoints; i++)
        {
            perturbed[i] = Array.IndexOf(seedIndices, i) >= 0 ? shared[i] : shared[i] ^ 1u;
        }

        var b = ConcatPoints(RandomPoints(rng, 130), perturbed, RandomPoints(rng, 50));

        var results = FingerprintComparer.FindMatchedRegions(
            a, b, DefaultMaxBitErrors, DefaultMaxTimeSkipSeconds,
            DefaultInvertedIndexShift, DefaultMinMatchDurationSeconds, CancellationToken.None);

        Assert.NotEmpty(results);

        // The longest region should cover the bulk of the shared intro.
        var longestSeconds = (results[0].EndTicks - results[0].StartTicks) / (double)TimeSpan.TicksPerSecond;
        Assert.True(
            longestSeconds >= DefaultMinMatchDurationSeconds,
            $"Expected a match of at least {DefaultMinMatchDurationSeconds}s, got {longestSeconds:F1}s");
    }

    private static uint[] RandomPoints(Random rng, int count)
    {
        var points = new uint[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = (uint)rng.Next();
        }

        return points;
    }

    private static byte[] ConcatPoints(params uint[][] segments)
    {
        var total = 0;
        foreach (var s in segments)
        {
            total += s.Length;
        }

        var result = new uint[total];
        var offset = 0;
        foreach (var s in segments)
        {
            Array.Copy(s, 0, result, offset, s.Length);
            offset += s.Length;
        }

        return MemoryMarshal.AsBytes(result.AsSpan()).ToArray();
    }

    private static byte[] CreateFingerprint(int pointCount, int seed)
    {
        var rng = new Random(seed);
        var bytes = new byte[pointCount * sizeof(uint)];
        rng.NextBytes(bytes);
        return bytes;
    }
}
