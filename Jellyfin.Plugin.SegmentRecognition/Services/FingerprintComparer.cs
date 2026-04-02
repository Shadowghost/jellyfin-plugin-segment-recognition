using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Compares chromaprint fingerprints using an inverted-index shift-based alignment algorithm.
/// Based on the approach used by intro-skipper: find alignment shifts via inverted index,
/// then verify matches point-by-point using Hamming distance.
/// </summary>
public static class FingerprintComparer
{
    /// <summary>
    /// Each chromaprint point represents roughly this many seconds.
    /// </summary>
    private const double SecondsPerPoint = 0.1238;

    /// <summary>
    /// Compares two fingerprints and returns matched regions using shift-based alignment.
    /// </summary>
    /// <param name="fingerprintA">First fingerprint raw bytes.</param>
    /// <param name="fingerprintB">Second fingerprint raw bytes.</param>
    /// <param name="maxBitErrors">Max Hamming distance per 32-bit point (e.g. 6).</param>
    /// <param name="maxTimeSkipSeconds">Max gap between consecutive matching points before breaking.</param>
    /// <param name="invertedIndexShift">Fuzzy tolerance for inverted index lookup.</param>
    /// <param name="minMatchDurationSeconds">Minimum match duration in seconds.</param>
    /// <param name="cancellationToken">Cancellation token to abort long-running comparisons.</param>
    /// <returns>Matched regions as (StartTicks, EndTicks) from fingerprint A's perspective.</returns>
    public static IReadOnlyList<(long StartTicks, long EndTicks)> FindMatchedRegions(
        byte[] fingerprintA,
        byte[] fingerprintB,
        int maxBitErrors,
        double maxTimeSkipSeconds,
        int invertedIndexShift,
        int minMatchDurationSeconds,
        CancellationToken cancellationToken)
    {
        var uintsA = MemoryMarshal.Cast<byte, uint>(fingerprintA.AsSpan());
        var uintsB = MemoryMarshal.Cast<byte, uint>(fingerprintB.AsSpan());

        if (uintsA.Length == 0 || uintsB.Length == 0)
        {
            return [];
        }

        // Build inverted index: map fingerprint value -> first occurrence index in B
        var invertedIndex = new Dictionary<uint, int>(uintsB.Length);
        for (int i = 0; i < uintsB.Length; i++)
        {
            invertedIndex.TryAdd(uintsB[i], i);
        }

        // Find candidate shifts by looking up each A point in B's inverted index
        var shiftCounts = new Dictionary<int, int>();
        for (int i = 0; i < uintsA.Length; i++)
        {
            var pointA = uintsA[i];

            // Try exact match and nearby values (bit-shift tolerance)
            for (int shift = -invertedIndexShift; shift <= invertedIndexShift; shift++)
            {
                var lookup = shift == 0 ? pointA : BitOperations.RotateLeft(pointA, shift);
                if (invertedIndex.TryGetValue(lookup, out var indexB))
                {
                    var alignmentShift = i - indexB;
                    shiftCounts.TryGetValue(alignmentShift, out var count);
                    shiftCounts[alignmentShift] = count + 1;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Try each candidate shift, find the best contiguous match
        var maxTimeSkipPoints = (int)(maxTimeSkipSeconds / SecondsPerPoint);
        var minMatchPoints = (int)(minMatchDurationSeconds / SecondsPerPoint);
        var bestMatch = (Start: -1, End: -1, Length: 0);
        var shiftsChecked = 0;

        foreach (var (alignmentShift, hitCount) in shiftCounts)
        {
            // Skip shifts with too few hits to possibly form a valid match
            if (hitCount < minMatchPoints / 4)
            {
                continue;
            }

            // Periodically check cancellation during CPU-bound work
            if (++shiftsChecked % 64 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var match = FindContiguousMatch(uintsA, uintsB, alignmentShift, maxBitErrors, maxTimeSkipPoints);
            if (match.Length > bestMatch.Length)
            {
                bestMatch = match;
            }
        }

        if (bestMatch.Length < minMatchPoints)
        {
            return [];
        }

        var startTicks = (long)(bestMatch.Start * SecondsPerPoint * TimeSpan.TicksPerSecond);
        var endTicks = (long)(bestMatch.End * SecondsPerPoint * TimeSpan.TicksPerSecond);
        return [(startTicks, endTicks)];
    }

    /// <summary>
    /// Given an alignment shift, walks through both fingerprints and finds the longest
    /// contiguous run of matching points.
    /// </summary>
    private static (int Start, int End, int Length) FindContiguousMatch(
        ReadOnlySpan<uint> a,
        ReadOnlySpan<uint> b,
        int shift,
        int maxBitErrors,
        int maxGapPoints)
    {
        // Determine overlapping range
        int startA = Math.Max(0, shift);
        int startB = Math.Max(0, -shift);
        int overlapLength = Math.Min(a.Length - startA, b.Length - startB);

        if (overlapLength <= 0)
        {
            return (-1, -1, 0);
        }

        // Collect indices (in A's coordinates) of matching points
        var matchingIndices = new List<int>();
        for (int i = 0; i < overlapLength; i++)
        {
            var bits = BitOperations.PopCount(a[startA + i] ^ b[startB + i]);
            if (bits <= maxBitErrors)
            {
                matchingIndices.Add(startA + i);
            }
        }

        if (matchingIndices.Count == 0)
        {
            return (-1, -1, 0);
        }

        // Find longest contiguous run (allowing gaps up to maxGapPoints)
        var bestStart = matchingIndices[0];
        var bestEnd = matchingIndices[0];
        var bestLength = 1;

        var runStart = matchingIndices[0];
        var runEnd = matchingIndices[0];
        var runLength = 1;

        for (int i = 1; i < matchingIndices.Count; i++)
        {
            if (matchingIndices[i] - matchingIndices[i - 1] <= maxGapPoints)
            {
                runEnd = matchingIndices[i];
                runLength++;
            }
            else
            {
                if (runLength > bestLength)
                {
                    bestStart = runStart;
                    bestEnd = runEnd;
                    bestLength = runLength;
                }

                runStart = matchingIndices[i];
                runEnd = matchingIndices[i];
                runLength = 1;
            }
        }

        if (runLength > bestLength)
        {
            bestStart = runStart;
            bestEnd = runEnd;
        }

        return (bestStart, bestEnd, bestEnd - bestStart);
    }
}
