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
        var a = aEmpty ? Array.Empty<byte>() : CreateFingerprint(200, seed: 1);
        var b = bEmpty ? Array.Empty<byte>() : CreateFingerprint(200, seed: 2);

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
    /// Cancellation should be respected.
    /// </summary>
    [Fact]
    public void CancelledToken_ThrowsOperationCancelledException()
    {
        var fingerprint = CreateFingerprint(200, seed: 42);
        var cts = new CancellationTokenSource();
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

    private static byte[] CreateFingerprint(int pointCount, int seed)
    {
        var rng = new Random(seed);
        var bytes = new byte[pointCount * sizeof(uint)];
        rng.NextBytes(bytes);
        return bytes;
    }
}
