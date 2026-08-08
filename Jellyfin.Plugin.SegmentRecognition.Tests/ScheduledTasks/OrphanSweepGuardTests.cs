using Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.ScheduledTasks;

/// <summary>
/// Tests for the guard on the orphan sweep, which deletes every cached row whose item is absent
/// from a single library query.
/// </summary>
/// <remarks>
/// Without the guard, a library query returning nothing - database locked, scan in flight, library
/// unmounted - wiped the whole analysis cache and forced hours of ffmpeg re-analysis. Keeping
/// stale rows is cheap; losing the cache is not.
/// </remarks>
public sealed class OrphanSweepGuardTests
{
    [Fact]
    public void EmptyLibraryResult_BlocksTheSweep()
    {
        var unsafeSweep = AnalyzeSegmentsTask.IsOrphanSweepUnsafe(
            knownItemCount: 500,
            libraryItemCount: 0,
            orphanCount: 500,
            out var reason);

        Assert.True(unsafeSweep);
        Assert.Contains("zero items", reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void MostOfCacheMissing_BlocksTheSweep()
    {
        var unsafeSweep = AnalyzeSegmentsTask.IsOrphanSweepUnsafe(
            knownItemCount: 1000,
            libraryItemCount: 400,
            orphanCount: 600,
            out _);

        Assert.True(unsafeSweep);
    }

    [Fact]
    public void OrdinaryRemoval_IsAllowed()
    {
        var unsafeSweep = AnalyzeSegmentsTask.IsOrphanSweepUnsafe(
            knownItemCount: 1000,
            libraryItemCount: 995,
            orphanCount: 5,
            out var reason);

        Assert.False(unsafeSweep);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void NothingToDelete_IsAllowed()
    {
        Assert.False(AnalyzeSegmentsTask.IsOrphanSweepUnsafe(1000, 1000, 0, out _));
    }

    /// <summary>
    /// The ratio check is skipped on tiny caches, where a single genuine removal trivially exceeds
    /// any percentage threshold.
    /// </summary>
    [Fact]
    public void SmallCache_IsNotSubjectToTheRatioCheck()
    {
        Assert.False(AnalyzeSegmentsTask.IsOrphanSweepUnsafe(
            knownItemCount: 3,
            libraryItemCount: 1,
            orphanCount: 2,
            out _));
    }

    /// <summary>
    /// Even on a tiny cache, a library that returned nothing is still refused - that is a read
    /// failure regardless of scale.
    /// </summary>
    [Fact]
    public void SmallCacheWithEmptyLibrary_IsStillBlocked()
    {
        Assert.True(AnalyzeSegmentsTask.IsOrphanSweepUnsafe(3, 0, 3, out _));
    }

    [Theory]
    [InlineData(100, 51, true)]   // just over half the cache
    [InlineData(100, 49, false)]  // just under
    public void RatioThresholdIsHalfTheCache(int known, int orphans, bool expectBlocked)
    {
        Assert.Equal(
            expectBlocked,
            AnalyzeSegmentsTask.IsOrphanSweepUnsafe(known, known - orphans, orphans, out _));
    }
}
