using System;
using System.Linq;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Storage;

/// <summary>
/// Regression tests for the <see cref="ChapterAnalysisResult"/> key.
/// </summary>
/// <remarks>
/// The table was originally keyed on (ItemId, SegmentType, MatchedChapterName). Because every row
/// from one source shares a single sentinel name, that made it impossible to store more than one
/// segment of a given type per item per source - so an EDL sidecar with two commercial breaks
/// threw a unique-constraint violation on the serving path.
/// </remarks>
public sealed class ChapterAnalysisResultKeyTests
{
    private static ChapterAnalysisResult Row(Guid itemId, int type, long startTicks, string source) => new()
    {
        ItemId = itemId,
        SegmentType = type,
        StartTicks = startTicks,
        EndTicks = startTicks + TimeSpan.TicksPerMinute,
        MatchedChapterName = source,
        ConfigHash = "hash",
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void MultipleSameTypeSegmentsFromOneSource_Persist()
    {
        using var fixture = new SegmentDbFixture();
        var itemId = Guid.NewGuid();

        using (var db = fixture.CreateContext())
        {
            // Three commercial breaks in one EDL file: same item, same type, same sentinel.
            db.ChapterAnalysisResults.AddRange(
                Row(itemId, 1, 60 * TimeSpan.TicksPerSecond, "edl-import"),
                Row(itemId, 1, 600 * TimeSpan.TicksPerSecond, "edl-import"),
                Row(itemId, 1, 1200 * TimeSpan.TicksPerSecond, "edl-import"));

            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            Assert.Equal(3, db.ChapterAnalysisResults.Count(r => r.ItemId == itemId));
        }
    }

    [Fact]
    public void DuplicateStartForSameTypeAndSource_IsRejected()
    {
        using var fixture = new SegmentDbFixture();
        var itemId = Guid.NewGuid();

        using var db = fixture.CreateContext();
        db.ChapterAnalysisResults.AddRange(
            Row(itemId, 1, 60 * TimeSpan.TicksPerSecond, "edl-import"),
            Row(itemId, 1, 60 * TimeSpan.TicksPerSecond, "edl-import"));

        // The natural key is still enforced, just widened by StartTicks - genuinely duplicated
        // rows remain an error rather than silently accumulating.
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void SurrogateKeysAreAssignedAutomatically()
    {
        using var fixture = new SegmentDbFixture();
        var itemId = Guid.NewGuid();

        using var db = fixture.CreateContext();
        var first = Row(itemId, 1, 0, "edl-import");
        var second = Row(itemId, 1, TimeSpan.TicksPerMinute, "edl-import");
        db.ChapterAnalysisResults.AddRange(first, second);
        db.SaveChanges();

        Assert.NotEqual(0, first.Id);
        Assert.NotEqual(0, second.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void DifferentSourcesForSameTypeAndStart_Coexist()
    {
        using var fixture = new SegmentDbFixture();
        var itemId = Guid.NewGuid();

        using var db = fixture.CreateContext();
        db.ChapterAnalysisResults.AddRange(
            Row(itemId, 4, 0, "blackframe-outro"),
            Row(itemId, 4, 0, "chromaprint-credits"));

        db.SaveChanges();

        Assert.Equal(2, db.ChapterAnalysisResults.Count(r => r.ItemId == itemId));
    }
}
