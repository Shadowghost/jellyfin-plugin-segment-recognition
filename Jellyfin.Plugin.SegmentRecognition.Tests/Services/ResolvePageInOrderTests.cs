using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Tests for the windowed page resolution behind the analyzed-items listing in timestamp order.
/// </summary>
/// <remarks>
/// The listing used to hand every matched container to the library on every request purely to
/// discover which ones still exist. These cover the replacement: the same slice, the same handling
/// of containers whose rows outlived their item, but only as many round trips as the page needs.
/// </remarks>
public sealed class ResolvePageInOrderTests
{
    [Fact]
    public void ReturnsTheRequestedSliceInOrder()
    {
        var ids = Ids(500);

        var page = Resolve(ids, startIndex: 100, limit: 50, alive: ids, out _);

        Assert.Equal(ids.Skip(100).Take(50), page);
    }

    [Fact]
    public void FirstPageCostsOneRoundTrip()
    {
        var ids = Ids(10_000);

        Resolve(ids, startIndex: 0, limit: 50, alive: ids, out var windows);

        // The point of the change: one window, not one query over all 10,000 ids.
        var window = Assert.Single(windows);
        Assert.Equal(200, window.Length);
    }

    [Fact]
    public void SkipsContainersTheLibraryNoLongerResolves()
    {
        var ids = Ids(300);

        // Every third container is a stale row whose item is gone.
        var alive = ids.Where((_, i) => i % 3 != 0).ToArray();

        var page = Resolve(ids, startIndex: 0, limit: 10, alive: alive, out _);

        Assert.Equal(alive.Take(10), page);
    }

    [Fact]
    public void WidensPastTheFirstWindowWhenTooManyAreStale()
    {
        var ids = Ids(600);

        // Only the last 50 still resolve, so a single 200-wide window cannot fill the page.
        var alive = ids.Skip(550).ToArray();

        var page = Resolve(ids, startIndex: 0, limit: 50, alive: alive, out var windows);

        Assert.Equal(alive, page);
        Assert.Equal(3, windows.Count);
    }

    [Fact]
    public void StopsWideningOnceThePageIsFull()
    {
        var ids = Ids(10_000);

        Resolve(ids, startIndex: 400, limit: 50, alive: ids, out var windows);

        // needed = 450, so the window widens to cover it and stops after the one pass.
        var window = Assert.Single(windows);
        Assert.Equal(450, window.Length);
    }

    [Fact]
    public void ReturnsEmptyWhenTheOffsetIsPastTheEnd()
    {
        var ids = Ids(30);

        Assert.Empty(Resolve(ids, startIndex: 100, limit: 50, alive: ids, out _));
    }

    [Fact]
    public void LastPageIsShortRatherThanEmpty()
    {
        var ids = Ids(120);

        var page = Resolve(ids, startIndex: 100, limit: 50, alive: ids, out _);

        Assert.Equal(20, page.Length);
    }

    [Fact]
    public void ObservesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SegmentDataQueryService.ResolvePageInOrder(Ids(10), 0, 5, w => w, cts.Token));
    }

    private static Guid[] Ids(int count) =>
        Enumerable.Range(1, count).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();

    private static Guid[] Resolve(
        IEnumerable<Guid> orderedIds,
        int startIndex,
        int limit,
        IEnumerable<Guid> alive,
        out List<Guid[]> windows)
    {
        var resolvable = new HashSet<Guid>(alive);
        var seen = new List<Guid[]>();
        windows = seen;

        return SegmentDataQueryService.ResolvePageInOrder(
            orderedIds,
            startIndex,
            limit,
            window =>
            {
                seen.Add(window);
                return window.Where(resolvable.Contains).ToArray();
            },
            CancellationToken.None);
    }
}
