using System;
using System.Data.Common;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Storage;

/// <summary>
/// Storage tests for the per-region match outcome recorded on <see cref="AnalysisStatus"/>.
/// </summary>
public sealed class MatchOutcomeStorageTests : IAsyncLifetime
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

    /// <summary>
    /// The columns are nullable so rows written before they existed stay valid, and a recorded
    /// outcome must survive the round-trip as the enum rather than a raw integer.
    /// </summary>
    [Fact]
    public async Task MatchOutcomesRoundTripAndDefaultToNull()
    {
        await using var db = new SegmentDbContext(_options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var recorded = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        db.AnalysisStatuses.AddRange(
            new AnalysisStatus
            {
                ItemId = recorded,
                ProviderName = "Chromaprint",
                AnalyzedAt = DateTime.UtcNow,
                HasResults = true,
                IntroOutcome = SegmentMatchOutcome.NoSharedAudio,
                OutroOutcome = SegmentMatchOutcome.Matched,
            },
            new AnalysisStatus
            {
                ItemId = untouched,
                ProviderName = "ChapterName",
                AnalyzedAt = DateTime.UtcNow,
                HasResults = true,
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.AnalysisStatuses.AsNoTracking()
            .ToDictionaryAsync(s => s.ItemId, TestContext.Current.CancellationToken);

        Assert.Equal(SegmentMatchOutcome.NoSharedAudio, rows[recorded].IntroOutcome);
        Assert.Equal(SegmentMatchOutcome.Matched, rows[recorded].OutroOutcome);

        // A provider that does not look for intros leaves both unset - not "Matched" (0).
        Assert.Null(rows[untouched].IntroOutcome);
        Assert.Null(rows[untouched].OutroOutcome);
    }
}
