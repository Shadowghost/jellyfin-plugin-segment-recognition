using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Tests for the segment-search sort order and the duration-to-ticks conversion.
/// </summary>
/// <remarks>
/// Every sort key the search exposes is non-unique - a bulk insert shares one CreatedAt to the
/// tick - so without a tie-break on the surrogate key SQLite was free to order ties differently
/// per query, which duplicated and dropped rows across pages. The duration filter multiplied an
/// unvalidated query parameter by 10 000 in an unchecked context, so a large value wrapped
/// negative and inverted the comparison.
/// </remarks>
public sealed class SegmentSearchOrderingTests : IDisposable
{
    private readonly SegmentDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void PagingOverTiedTimestamps_VisitsEveryRowExactlyOnce()
    {
        // All 30 rows share a CreatedAt, which is what a single analysis pass actually writes.
        var createdAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        Seed(Enumerable.Range(0, 30).Select(i => new ChapterAnalysisResult
        {
            ItemId = Guid.NewGuid(),
            SegmentType = 1,
            StartTicks = i * 1000,
            EndTicks = (i * 1000) + 500,
            MatchedChapterName = $"Chapter {i}",
            CreatedAt = createdAt,
        }));

        var seen = new List<int>();
        for (var page = 0; page < 3; page++)
        {
            using var db = _fixture.CreateContext();
            var ordered = SegmentDataQueryService.ApplySegmentOrder(db.ChapterAnalysisResults, "createdAt", descending: true);
            seen.AddRange(ordered.Skip(page * 10).Take(10).Select(r => r.Id).ToList());
        }

        Assert.Equal(30, seen.Count);
        Assert.Equal(30, seen.Distinct().Count());
    }

    [Fact]
    public void TiedDurations_AreOrderedDeterministically()
    {
        Seed(Enumerable.Range(0, 10).Select(i => new ChapterAnalysisResult
        {
            ItemId = Guid.NewGuid(),
            SegmentType = 1,
            StartTicks = i * 10_000,
            // Identical duration on every row.
            EndTicks = (i * 10_000) + 5_000,
            MatchedChapterName = $"Chapter {i}",
            CreatedAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc),
        }));

        List<int> Read()
        {
            using var db = _fixture.CreateContext();
            return SegmentDataQueryService
                .ApplySegmentOrder(db.ChapterAnalysisResults, "duration", descending: false)
                .Select(r => r.Id)
                .ToList();
        }

        Assert.Equal(Read(), Read());
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, TimeSpan.TicksPerMillisecond)]
    [InlineData(1_000L, 1_000 * TimeSpan.TicksPerMillisecond)]
    public void MillisecondsToTicks_ConvertsNormalValues(long milliseconds, long expected)
    {
        Assert.Equal(expected, SegmentDataQueryService.MillisecondsToTicksSaturating(milliseconds));
    }

    [Fact]
    public void MillisecondsToTicks_SaturatesInsteadOfWrappingNegative()
    {
        // 1e15 ms x 10 000 ticks/ms overflows long; unchecked it wrapped to a negative bound,
        // which turned "at least this long" into "match everything".
        var ticks = SegmentDataQueryService.MillisecondsToTicksSaturating(1_000_000_000_000_000L);

        Assert.Equal(long.MaxValue, ticks);
        Assert.True(ticks > 0);
    }

    [Fact]
    public void MillisecondsToTicks_SaturatesAtTheNegativeBound()
    {
        var ticks = SegmentDataQueryService.MillisecondsToTicksSaturating(-1_000_000_000_000_000L);

        Assert.Equal(long.MinValue, ticks);
    }

    private void Seed(IEnumerable<ChapterAnalysisResult> rows)
    {
        using var db = _fixture.CreateContext();
        db.ChapterAnalysisResults.AddRange(rows);
        db.SaveChanges();
    }
}
