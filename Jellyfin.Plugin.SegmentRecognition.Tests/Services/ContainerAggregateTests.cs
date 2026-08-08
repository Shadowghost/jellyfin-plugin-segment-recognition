using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Services;

/// <summary>
/// Tests for the raw container roll-up behind the analyzed-items listing.
/// </summary>
/// <remarks>
/// The query is hand-written SQL, so nothing but a real SQLite engine can tell us whether it
/// parses, whether the unused <c>json_each</c> parameter is tolerated when no parent filter is
/// given, or whether the id join matches the casing EF actually writes.
/// </remarks>
public sealed class ContainerAggregateTests : IDisposable
{
    private static readonly DateTime _early = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _late = new(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc);

    private readonly SegmentDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RollsLeavesUpToTheirContainer()
    {
        var series = Guid.NewGuid();
        var episodeA = Guid.NewGuid();
        var episodeB = Guid.NewGuid();

        Seed(
            Status(episodeA, series, "ChapterName", _early, hasResults: true),
            Status(episodeB, series, "BlackFrame", _late, hasResults: true));

        var rows = await RunAsync().ConfigureAwait(true);

        var row = Assert.Single(rows);
        Assert.Equal(series, row.ItemId);
        Assert.Equal(1, row.HasResultsInt);
        Assert.Equal(_late, DateTime.SpecifyKind(row.LastAnalyzedAtRaw!.Value, DateTimeKind.Utc));
        Assert.Equal(
            new[] { "BlackFrame", "ChapterName" },
            row.ProvidersConcat!.Split('|').OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ExcludesRowsAwaitingContainerBackfill()
    {
        Seed(Status(Guid.NewGuid(), Guid.Empty, "ChapterName", _early, hasResults: true));

        Assert.Empty(await RunAsync().ConfigureAwait(true));
    }

    [Fact]
    public async Task ReportsResultsAndErrorsOnTheSameContainer()
    {
        var series = Guid.NewGuid();

        // One provider produced segments; a different one failed. The container has both, and a
        // filter applied per-row before the roll-up would have reported neither correctly.
        Seed(
            Status(series, series, "ChapterName", _early, hasResults: true),
            Status(series, series, "Chromaprint", _late, hasResults: false, lastError: "ffmpeg exploded"));

        var row = Assert.Single(await RunAsync().ConfigureAwait(true));

        Assert.Equal(1, row.HasResultsInt);
        Assert.Equal(1, row.HasErrorInt);
        Assert.Equal("ChapterName", row.ProvidersConcat);
    }

    [Fact]
    public async Task ProviderFilterRestrictsToThatProvider()
    {
        var movie = Guid.NewGuid();
        Seed(
            Status(movie, movie, "ChapterName", _early, hasResults: true),
            Status(movie, movie, "BlackFrame", _late, hasResults: true));

        var row = Assert.Single(await RunAsync(providerFilter: "BlackFrame").ConfigureAwait(true));

        Assert.Equal("BlackFrame", row.ProvidersConcat);
    }

    [Fact]
    public async Task AnalyzedSinceExcludesOlderRows()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        Seed(
            Status(older, older, "ChapterName", _early, hasResults: true),
            Status(newer, newer, "ChapterName", _late, hasResults: true));

        var rows = await RunAsync(analyzedSinceUtc: _late.AddDays(-1)).ConfigureAwait(true);

        Assert.Equal(newer, Assert.Single(rows).ItemId);
    }

    [Fact]
    public async Task ParentFilterKeepsOnlyTheListedLeaves()
    {
        var seriesA = Guid.NewGuid();
        var seriesB = Guid.NewGuid();
        var episodeA = Guid.NewGuid();
        var episodeB = Guid.NewGuid();

        Seed(
            Status(episodeA, seriesA, "ChapterName", _early, hasResults: true),
            Status(episodeB, seriesB, "ChapterName", _early, hasResults: true));

        var json = JsonSerializer.Serialize(new[] { episodeA.ToString("D").ToUpperInvariant() });
        var rows = await RunAsync(allowedIdsJson: json).ConfigureAwait(true);

        Assert.Equal(seriesA, Assert.Single(rows).ItemId);
    }

    [Fact]
    public async Task ParentFilterMatchesRegardlessOfIdCasing()
    {
        var series = Guid.NewGuid();
        var episode = Guid.NewGuid();
        Seed(Status(episode, series, "ChapterName", _early, hasResults: true));

        // The join is COLLATE NOCASE so a row stored in another casing is not silently dropped.
        var json = JsonSerializer.Serialize(new[] { episode.ToString("D").ToLowerInvariant() });
        var rows = await RunAsync(allowedIdsJson: json).ConfigureAwait(true);

        Assert.Equal(series, Assert.Single(rows).ItemId);
    }

    [Fact]
    public async Task ChunkedParentFiltersMergeIntoOneContainer()
    {
        var series = Guid.NewGuid();
        var episodeA = Guid.NewGuid();
        var episodeB = Guid.NewGuid();

        Seed(
            Status(episodeA, series, "ChapterName", _early, hasResults: true),
            Status(episodeB, series, "Chromaprint", _late, hasResults: false, lastError: "boom"));

        // Simulates two chunks of one id each: each pass sees a partial view of the container.
        var first = Assert.Single(await RunAsync(
            allowedIdsJson: JsonSerializer.Serialize(new[] { episodeA.ToString("D").ToUpperInvariant() })).ConfigureAwait(true));
        var second = Assert.Single(await RunAsync(
            allowedIdsJson: JsonSerializer.Serialize(new[] { episodeB.ToString("D").ToUpperInvariant() })).ConfigureAwait(true));

        Assert.Equal(series, first.ItemId);
        Assert.Equal(series, second.ItemId);
        Assert.Equal(1, first.HasResultsInt);
        Assert.Equal(0, first.HasErrorInt);
        Assert.Equal(0, second.HasResultsInt);
        Assert.Equal(1, second.HasErrorInt);
    }

    private static AnalysisStatus Status(
        Guid itemId,
        Guid containerId,
        string provider,
        DateTime analyzedAt,
        bool hasResults,
        string? lastError = null) => new()
        {
            ItemId = itemId,
            ContainerId = containerId,
            ProviderName = provider,
            AnalyzedAt = analyzedAt,
            HasResults = hasResults,
            LastError = lastError,
            LastErrorAt = lastError is null ? null : analyzedAt,
        };

    private void Seed(params AnalysisStatus[] statuses)
    {
        using var db = _fixture.CreateContext();
        db.AnalysisStatuses.AddRange(statuses);
        db.SaveChanges();
    }

    private async Task<List<SegmentDataQueryService.AggregateRow>> RunAsync(
        string? allowedIdsJson = null,
        string? providerFilter = null,
        DateTime? analyzedSinceUtc = null)
    {
        using var db = _fixture.CreateContext();
        return await SegmentDataQueryService
            .RunAggregateAsync(db, allowedIdsJson, providerFilter, analyzedSinceUtc, CancellationToken.None)
            .ConfigureAwait(true);
    }
}
