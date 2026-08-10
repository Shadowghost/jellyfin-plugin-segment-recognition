using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Tests for the per-item mutual exclusion used by recalculation jobs.
/// </summary>
/// <remarks>
/// The service documented this guarantee but nothing ever called the locking helper. The job
/// idempotency check only rejects a request whose target set matches an in-flight one exactly, so
/// overlapping requests - a series and one of its episodes - both reached analysis for the same
/// item and raced on the same rows.
/// </remarks>
public sealed class RecalculationLockTests : IDisposable
{
    private readonly SegmentDbFixture _fixture = new();
    private readonly RecalculationJobService _service;

    public RecalculationLockTests()
    {
        _service = new RecalculationJobService(_fixture.Factory, NullLogger<RecalculationJobService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task ConcurrentHoldersOfTheSameKey_AreSerialized()
    {
        var key = RecalculationJobService.ItemLockKey(Guid.NewGuid());
        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = _service.RunWithLockAsync(
            key,
            async () =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            },
            CancellationToken.None);

        await firstEntered.Task;

        var secondRan = false;
        var second = await _service.RunWithLockAsync(
            key,
            () =>
            {
                secondRan = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(second);
        Assert.False(secondRan);

        releaseFirst.SetResult();
        Assert.True(await first);
    }

    [Fact]
    public async Task DifferentKeys_DoNotBlockEachOther()
    {
        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = _service.RunWithLockAsync(
            RecalculationJobService.ItemLockKey(Guid.NewGuid()),
            async () =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            },
            CancellationToken.None);

        await firstEntered.Task;

        var second = await _service.RunWithLockAsync(
            RecalculationJobService.ItemLockKey(Guid.NewGuid()),
            () => Task.CompletedTask,
            CancellationToken.None);

        Assert.True(second);

        releaseFirst.SetResult();
        await first;
    }

    [Fact]
    public async Task LockIsReleasedAfterCompletion()
    {
        var key = RecalculationJobService.ItemLockKey(Guid.NewGuid());

        Assert.True(await _service.RunWithLockAsync(key, () => Task.CompletedTask, CancellationToken.None));
        Assert.True(await _service.RunWithLockAsync(key, () => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task LockIsReleasedWhenTheActionThrows()
    {
        var key = RecalculationJobService.ItemLockKey(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RunWithLockAsync(key, () => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.True(await _service.RunWithLockAsync(key, () => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public void ItemLockKeyIsStableAndDistinct()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(RecalculationJobService.ItemLockKey(a), RecalculationJobService.ItemLockKey(a));
        Assert.NotEqual(RecalculationJobService.ItemLockKey(a), RecalculationJobService.ItemLockKey(b));
    }
}
