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
    /// Minimum number of inverted-index votes a candidate alignment shift needs before it is
    /// worth verifying with the (expensive) point-by-point Hamming scan.
    /// <para>
    /// The inverted index only votes when it finds a (near-)exact 32-bit point match, so for
    /// intros that are similar-on-average but noisy (e.g. surround→mono downmixes, variable-length
    /// cold opens that push the title sequence to different offsets) the *correct* shift may collect
    /// only one or two exact votes even though the real intro matches well within
    /// <c>maxBitErrors</c>. A high threshold here starves those matches entirely - the true shift is
    /// discarded before <see cref="CollectContiguousMatches"/> ever runs. Keep this low and let the
    /// Hamming scan (which enforces bit-error, gap, and minimum-duration constraints) be the real
    /// filter; spurious shifts simply produce no qualifying region. Exact 32-bit collisions between
    /// unrelated fingerprints are vanishingly rare, so 2 votes is enough to skip pure noise cheaply.
    /// </para>
    /// </summary>
    private const int MinShiftVotes = 2;

    /// <summary>
    /// Maximum number of positions indexed per distinct fingerprint value.
    /// <para>
    /// Points repeat legitimately, but a long run of an identical value (digital silence, a held
    /// tone) would otherwise make one value contribute <c>occurrences × matches</c> votes and
    /// swamp the shift tally - as well as making index construction quadratic in that run's
    /// length.
    /// </para>
    /// </summary>
    private const int MaxOccurrencesPerPoint = 16;

    /// <summary>
    /// Compares two fingerprints and returns matched regions using shift-based alignment.
    /// Every contiguous run at or above <paramref name="minMatchDurationSeconds"/> is returned,
    /// ordered longest-first, so the caller can pick a region that fits its own duration
    /// constraints (e.g. intro window of 5–120 s) instead of being forced onto the single
    /// longest match - which can span the shared OP plus post-OP content and overshoot the
    /// window.
    /// </summary>
    /// <param name="fingerprintA">First fingerprint raw bytes.</param>
    /// <param name="fingerprintB">Second fingerprint raw bytes.</param>
    /// <param name="maxBitErrors">Max Hamming distance per 32-bit point (e.g. 6).</param>
    /// <param name="maxTimeSkipSeconds">Max gap between consecutive matching points before breaking.</param>
    /// <param name="invertedIndexShift">Fuzzy tolerance for inverted index lookup.</param>
    /// <param name="minMatchDurationSeconds">Minimum match duration in seconds.</param>
    /// <param name="cancellationToken">Cancellation token to abort long-running comparisons.</param>
    /// <returns>Matched regions as (StartTicks, EndTicks) from fingerprint A's perspective, ordered by descending length.</returns>
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

        // Build inverted index: map fingerprint value -> EVERY occurrence index in B.
        // Indexing only the first occurrence silently discarded every alignment that depended on
        // a later repeat of the same point, which is common in music-heavy intros.
        var invertedIndex = new Dictionary<uint, List<int>>(uintsB.Length);
        for (int i = 0; i < uintsB.Length; i++)
        {
            if (invertedIndex.TryGetValue(uintsB[i], out var occurrences))
            {
                // Cap the fan-out: a fingerprint with a long constant stretch (digital silence)
                // would otherwise contribute a quadratic number of votes and dominate the tally.
                if (occurrences.Count < MaxOccurrencesPerPoint)
                {
                    occurrences.Add(i);
                }
            }
            else
            {
                invertedIndex[uintsB[i]] = [i];
            }
        }

        // Find candidate shifts by looking up each A point in B's inverted index.
        var shiftCounts = new Dictionary<int, int>();
        for (int i = 0; i < uintsA.Length; i++)
        {
            var pointA = uintsA[i];

            // Fuzz the lookup arithmetically. The previous implementation rotated the 32-bit
            // point, which produces a value with no acoustic relationship to the original - it
            // added lookups that could only ever hit by coincidence, inflating the candidate-shift
            // set (and the cost of the Hamming scan that follows) without improving recall.
            for (int delta = -invertedIndexShift; delta <= invertedIndexShift; delta++)
            {
                var lookup = (uint)unchecked(pointA + delta);
                if (!invertedIndex.TryGetValue(lookup, out var occurrences))
                {
                    continue;
                }

                foreach (var indexB in occurrences)
                {
                    var alignmentShift = i - indexB;
                    shiftCounts.TryGetValue(alignmentShift, out var count);
                    shiftCounts[alignmentShift] = count + 1;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Try each candidate shift, collect every qualifying contiguous match.
        var maxTimeSkipPoints = (int)(maxTimeSkipSeconds / SecondsPerPoint);
        var minMatchPoints = (int)(minMatchDurationSeconds / SecondsPerPoint);
        var regions = new List<(int Start, int End, int Length)>();
        var shiftsChecked = 0;

        foreach (var (alignmentShift, hitCount) in shiftCounts)
        {
            // Skip shifts with too few exact-match votes to be worth a full Hamming scan.
            // See MinShiftVotes for why this must stay low rather than scale with minMatchPoints.
            if (hitCount < MinShiftVotes)
            {
                continue;
            }

            // Periodically check cancellation during CPU-bound work
            if (++shiftsChecked % 64 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            CollectContiguousMatches(uintsA, uintsB, alignmentShift, maxBitErrors, maxTimeSkipPoints, minMatchPoints, regions);
        }

        if (regions.Count == 0)
        {
            return [];
        }

        // Longest-first so callers that just want the biggest region still get it, and
        // duration-bounded callers can scan down until they find one in-window.
        regions.Sort(static (a, b) => b.Length.CompareTo(a.Length));

        var result = new (long StartTicks, long EndTicks)[regions.Count];
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            var startTicks = (long)(r.Start * SecondsPerPoint * TimeSpan.TicksPerSecond);
            var endTicks = (long)(r.End * SecondsPerPoint * TimeSpan.TicksPerSecond);
            result[i] = (startTicks, endTicks);
        }

        return result;
    }

    /// <summary>
    /// Given an alignment shift, walks through both fingerprints and appends every contiguous
    /// run of matching points whose length is at least <paramref name="minMatchPoints"/>.
    /// </summary>
    private static void CollectContiguousMatches(
        ReadOnlySpan<uint> a,
        ReadOnlySpan<uint> b,
        int shift,
        int maxBitErrors,
        int maxGapPoints,
        int minMatchPoints,
        List<(int Start, int End, int Length)> sink)
    {
        // Determine overlapping range
        int startA = Math.Max(0, shift);
        int startB = Math.Max(0, -shift);
        int overlapLength = Math.Min(a.Length - startA, b.Length - startB);

        if (overlapLength <= 0)
        {
            return;
        }

        // Walk the overlap in one pass, tracking the current run. Every time the gap to the
        // next match exceeds maxGapPoints we close out the run and emit it if long enough.
        int runStart = -1;
        int runEnd = -1;

        for (int i = 0; i < overlapLength; i++)
        {
            var bits = BitOperations.PopCount(a[startA + i] ^ b[startB + i]);
            var isMatch = bits <= maxBitErrors;
            if (!isMatch)
            {
                continue;
            }

            var idx = startA + i;
            if (runStart < 0)
            {
                runStart = idx;
                runEnd = idx;
            }
            else if (idx - runEnd <= maxGapPoints)
            {
                runEnd = idx;
            }
            else
            {
                var length = (runEnd - runStart) + 1;
                if (length >= minMatchPoints)
                {
                    sink.Add((runStart, runEnd, length));
                }

                runStart = idx;
                runEnd = idx;
            }
        }

        if (runStart >= 0)
        {
            // Inclusive count, matching the in-loop emit above: a run covering indices
            // [start, end] contains end - start + 1 points, not end - start. Counting intervals
            // made every run one point shorter than it really was, so a match of exactly the
            // configured minimum duration was rejected.
            var length = (runEnd - runStart) + 1;
            if (length >= minMatchPoints)
            {
                sink.Add((runStart, runEnd, length));
            }
        }
    }
}
