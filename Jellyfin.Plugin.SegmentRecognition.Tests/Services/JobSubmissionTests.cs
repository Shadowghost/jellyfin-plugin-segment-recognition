using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Tests for job submission idempotency and for the per-target lock registry not growing without
/// bound.
/// </summary>
/// <remarks>
/// The duplicate check used to be a separate call the controller made before submitting, so two
/// identical requests arriving together both saw an idle registry and both started the same work.
/// The lock registry never removed entries, retaining one semaphore per item id for the lifetime
/// of the process - tens of thousands after a full-library recalculation.
/// </remarks>
public sealed class JobSubmissionTests : IDisposable
{
    private readonly SegmentDbFixture _fixture = new();
    private readonly RecalculationJobService _service;

    public JobSubmissionTests()
    {
        _service = new RecalculationJobService(_fixture.Factory, NullLogger<RecalculationJobService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task TrySubmit_RejectsAnIdenticalActiveJob()
    {
        var targets = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        Assert.True(_service.TrySubmit(
            "Recalculate",
            targets,
            totalLeaves: 2,
            work: async (_, _) =>
            {
                started.SetResult();
                await release.Task;
            },
            job: out var first));

        await started.Task;

        // Same targets in a different order: the match is order-insensitive.
        var accepted = _service.TrySubmit(
            "Recalculate",
            targets.Reverse().ToArray(),
            totalLeaves: 2,
            work: (_, _) => Task.CompletedTask,
            job: out var conflicting);

        Assert.False(accepted);
        Assert.Equal(first.JobId, conflicting.JobId);

        release.SetResult();
    }

    [Fact]
    public void TrySubmit_AcceptsADifferentTargetSet()
    {
        Assert.True(_service.TrySubmit(
            "Recalculate",
            new[] { Guid.NewGuid() },
            totalLeaves: 1,
            work: (_, _) => Task.CompletedTask,
            job: out _));

        Assert.True(_service.TrySubmit(
            "Recalculate",
            new[] { Guid.NewGuid() },
            totalLeaves: 1,
            work: (_, _) => Task.CompletedTask,
            job: out _));
    }

    [Fact]
    public void TrySubmit_IsAtomicUnderConcurrentCallers()
    {
        var targets = new[] { Guid.NewGuid() };
        var release = new TaskCompletionSource();
        var accepted = 0;

        // All callers race on the same target set; exactly one may win.
        Parallel.For(0, 16, index =>
        {
            if (_service.TrySubmit(
                    "Recalculate",
                    targets,
                    totalLeaves: 1,
                    work: async (_, _) => await release.Task,
                    job: out _))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        release.SetResult();
        Assert.Equal(1, accepted);
    }

    [Fact]
    public async Task LockRegistry_DrainsAfterTheHoldersRelease()
    {
        var keys = Enumerable.Range(0, 50)
            .Select(_ => RecalculationJobService.ItemLockKey(Guid.NewGuid()))
            .ToArray();

        foreach (var key in keys)
        {
            Assert.True(await _service.RunWithLockAsync(key, () => Task.CompletedTask, CancellationToken.None));
        }

        Assert.Equal(0, _service.RetainedLockCount);
    }

    [Fact]
    public async Task LockRegistry_DrainsWhenTheActionThrows()
    {
        var key = RecalculationJobService.ItemLockKey(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RunWithLockAsync(key, () => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Equal(0, _service.RetainedLockCount);
    }

    [Fact]
    public async Task LockRegistry_RetainsAnEntryOnlyWhileItIsHeld()
    {
        var key = RecalculationJobService.ItemLockKey(Guid.NewGuid());
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var holder = _service.RunWithLockAsync(
            key,
            async () =>
            {
                entered.SetResult();
                await release.Task;
            },
            CancellationToken.None);

        await entered.Task;
        Assert.Equal(1, _service.RetainedLockCount);

        // A rejected contender must not leave the entry pinned behind it.
        Assert.False(await _service.RunWithLockAsync(key, () => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, _service.RetainedLockCount);

        release.SetResult();
        Assert.True(await holder);
        Assert.Equal(0, _service.RetainedLockCount);
    }

    [Fact]
    public async Task ConcurrentHoldersOfDistinctKeys_AreAllReclaimed()
    {
        var keys = Enumerable.Range(0, 32)
            .Select(_ => RecalculationJobService.ItemLockKey(Guid.NewGuid()))
            .ToList();

        await Task.WhenAll(keys.Select(k =>
            _service.RunWithLockAsync(k, () => Task.Delay(5), CancellationToken.None)));

        Assert.Equal(0, _service.RetainedLockCount);
    }

    [Fact]
    public async Task Submit_StillReturnsTheExistingJobWhenADuplicateIsActive()
    {
        var targets = new List<Guid> { Guid.NewGuid() };
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var first = _service.Submit(
            "Rematch",
            targets,
            totalLeaves: 1,
            work: async (_, _) =>
            {
                started.SetResult();
                await release.Task;
            });

        await started.Task;

        var second = _service.Submit("Rematch", targets, totalLeaves: 1, work: (_, _) => Task.CompletedTask);

        Assert.Equal(first.JobId, second.JobId);
        release.SetResult();
    }
}
