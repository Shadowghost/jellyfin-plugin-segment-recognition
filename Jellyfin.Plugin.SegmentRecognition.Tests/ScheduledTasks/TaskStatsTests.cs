using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.ScheduledTasks;

public class TaskStatsTests
{
    [Fact]
    public void AllCounters_InitializeToZero()
    {
        var stats = new TaskStats();

        Assert.Equal(0, stats.ChapterAnalyzed);
        Assert.Equal(0, stats.BlackFrameAnalyzed);
        Assert.Equal(0, stats.AnalysisSkipped);
        Assert.Equal(0, stats.AnalysisFailed);
        Assert.Equal(0, stats.FingerprintsGenerated);
        Assert.Equal(0, stats.SeasonsAnalyzed);
        Assert.Equal(0, stats.Pushed);
        Assert.Equal(0, stats.PushSkipped);
        Assert.Equal(0, stats.TotalWork);
    }

    [Fact]
    public void IncrementChapterAnalyzed_IncrementsCounter()
    {
        var stats = new TaskStats();

        stats.IncrementChapterAnalyzed();
        stats.IncrementChapterAnalyzed();

        Assert.Equal(2, stats.ChapterAnalyzed);
    }

    [Fact]
    public void IncrementBlackFrameAnalyzed_IncrementsCounter()
    {
        var stats = new TaskStats();

        stats.IncrementBlackFrameAnalyzed();

        Assert.Equal(1, stats.BlackFrameAnalyzed);
    }

    [Fact]
    public void IncrementAnalysisFailed_IncrementsCounter()
    {
        var stats = new TaskStats();

        stats.IncrementAnalysisFailed();
        stats.IncrementAnalysisFailed();
        stats.IncrementAnalysisFailed();

        Assert.Equal(3, stats.AnalysisFailed);
    }

    [Fact]
    public void TotalWork_SumsChapterBlackFrameAndFingerprints()
    {
        var stats = new TaskStats();

        stats.IncrementChapterAnalyzed();       // +1
        stats.IncrementBlackFrameAnalyzed();    // +1
        stats.IncrementFingerprintsGenerated(); // +1
        stats.IncrementFingerprintsGenerated(); // +1

        Assert.Equal(4, stats.TotalWork);
    }

    [Fact]
    public void TotalWork_ExcludesSkippedFailedPushed()
    {
        var stats = new TaskStats();

        stats.IncrementChapterAnalyzed();  // counts toward TotalWork
        stats.IncrementAnalysisSkipped();  // should NOT count
        stats.IncrementAnalysisFailed();   // should NOT count
        stats.IncrementPushed();           // should NOT count
        stats.IncrementPushSkipped();      // should NOT count
        stats.IncrementSeasonsAnalyzed();  // should NOT count

        Assert.Equal(1, stats.TotalWork);
    }

    [Fact]
    public void PushedItemIds_TracksDistinctItems()
    {
        var stats = new TaskStats();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        stats.PushedItemIds.TryAdd(id1, 0);
        stats.PushedItemIds.TryAdd(id2, 0);
        stats.PushedItemIds.TryAdd(id1, 0); // duplicate — should not add

        Assert.Equal(2, stats.PushedItemIds.Count);
        Assert.True(stats.PushedItemIds.ContainsKey(id1));
        Assert.True(stats.PushedItemIds.ContainsKey(id2));
    }

    [Fact]
    public async Task ConcurrentIncrements_AreThreadSafe()
    {
        var stats = new TaskStats();
        const int iterations = 10_000;

        await Parallel.ForAsync(0, iterations, (_, _) =>
        {
            stats.IncrementChapterAnalyzed();
            stats.IncrementBlackFrameAnalyzed();
            stats.IncrementFingerprintsGenerated();
            return ValueTask.CompletedTask;
        });

        Assert.Equal(iterations, stats.ChapterAnalyzed);
        Assert.Equal(iterations, stats.BlackFrameAnalyzed);
        Assert.Equal(iterations, stats.FingerprintsGenerated);
        Assert.Equal(iterations * 3, stats.TotalWork);
    }
}
