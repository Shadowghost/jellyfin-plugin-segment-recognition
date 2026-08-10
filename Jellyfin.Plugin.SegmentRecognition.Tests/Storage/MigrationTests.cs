using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Storage;

/// <summary>
/// Applies the migration chain to an empty database.
/// </summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private readonly DbConnection _connection = new SqliteConnection("Data Source=:memory:");
    private DbContextOptions<SegmentDbContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        _options = new DbContextOptionsBuilder<SegmentDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task MigrationChain_AppliesToAnEmptyDatabase()
    {
        await using var db = new SegmentDbContext(_options);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var applied = (await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ToList();
        Assert.NotEmpty(applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Contains("20260808120000_AddAnalysisStatusContainerIdIndex", applied, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ContainerRollupIsCoveredByAnIndex()
    {
        await using var db = new SegmentDbContext(_options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var indexes = await ReadIndexNamesAsync("AnalysisStatuses");

        // The analyzed-items listing groups by ContainerId on every request; unindexed that is a
        // full scan of the status table into a temporary B-tree, and indexed on ContainerId alone
        // it is still one rowid lookup per row into a table interleaved with fingerprint blobs.
        Assert.Contains("IX_AnalysisStatuses_ContainerRollup", indexes, StringComparer.Ordinal);

        // Subsumed by the above as its leftmost prefix; keeping it only cost write amplification.
        Assert.DoesNotContain("IX_AnalysisStatuses_ContainerId", indexes, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ContainerRollupIndexCoversTheAggregate()
    {
        await using var db = new SegmentDbContext(_options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // Asserting the plan, not just the index: a column dropped from the index or added to the
        // aggregate's SELECT list would silently put the rowid lookups back.
        var plan = await ReadQueryPlanAsync(
            @"SELECT s.ContainerId,
                     MAX(s.AnalyzedAt),
                     MAX(CASE WHEN s.HasResults = 1 THEN 1 ELSE 0 END),
                     MAX(CASE WHEN s.LastError IS NOT NULL THEN 1 ELSE 0 END),
                     group_concat(CASE WHEN s.HasResults = 1 THEN s.ProviderName END, '|')
              FROM AnalysisStatuses s
              WHERE s.ContainerId <> '00000000-0000-0000-0000-000000000000'
              GROUP BY s.ContainerId").ConfigureAwait(true);

        Assert.Contains("COVERING INDEX IX_AnalysisStatuses_ContainerRollup", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigratedSchemaAcceptsWritesAndReads()
    {
        await using var db = new SegmentDbContext(_options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        db.AnalysisStatuses.Add(new Jellyfin.Plugin.SegmentRecognition.Data.Entities.AnalysisStatus
        {
            ItemId = Guid.NewGuid(),
            ContainerId = Guid.NewGuid(),
            ProviderName = "ChapterName",
            AnalyzedAt = DateTime.UtcNow,
            HasResults = true,
            LastError = "boom",
            LastErrorAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = Assert.Single(await db.AnalysisStatuses.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("boom", stored.LastError);
        Assert.NotEqual(Guid.Empty, stored.ContainerId);
    }


    private async Task<List<string>> ReadIndexNamesAsync(string table)
    {
        var names = new List<string>();
        await using var command = _connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list('{table}')";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return names;
    }

    private async Task<string> ReadQueryPlanAsync(string sql)
    {
        var lines = new List<string>();
        await using var command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return string.Join('\n', lines);
    }
}
