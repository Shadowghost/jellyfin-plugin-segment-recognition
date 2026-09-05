using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Storage;

/// <summary>
/// Covers the migration that drops what the removed intro-skipper import task left behind.
/// </summary>
public sealed class IntroSkipperImportCleanupTests : IAsyncLifetime
{
    /// <summary>
    /// The last migration written before the import was removed. The database is brought up to
    /// this point, seeded as an install that ran the import, and only then migrated to HEAD.
    /// </summary>
    private const string BeforeCleanup = "20260808222229_AddAnalysisStatusMatchOutcomes";

    private const string ImportSentinel = "intro-skipper import";

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
    /// Imported rows were never served - <c>ChapterNameProvider</c> excluded their sentinel - so
    /// they have to go before the sentinel disappears from that exclusion list, together with the
    /// fingerprints and statuses the import wrote for the same items. Everything the plugin
    /// produced itself has to survive untouched.
    /// </summary>
    [Fact]
    public async Task CleanupDropsImportedRowsAndSparesNativeOnes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var imported = Guid.NewGuid();
        var native = Guid.NewGuid();

        await using (var db = new SegmentDbContext(_options))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeCleanup, cancellationToken);

            db.ChapterAnalysisResults.AddRange(
                NewChapterRow(imported, ImportSentinel),
                NewChapterRow(native, "Opening"));
            db.ChromaprintResults.AddRange(
                NewFingerprint(imported),
                NewFingerprint(native));
            db.AnalysisStatuses.AddRange(
                NewStatus(imported, ProviderNames.ChapterName),
                NewStatus(imported, ProviderNames.Chromaprint),
                NewStatus(native, ProviderNames.ChapterName));

            await db.SaveChangesAsync(cancellationToken);
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
        }

        await using var verify = new SegmentDbContext(_options);

        Assert.Empty(await verify.ChapterAnalysisResults
            .Where(r => r.MatchedChapterName == ImportSentinel)
            .ToListAsync(cancellationToken));
        Assert.Empty(await verify.ChromaprintResults.Where(r => r.ItemId == imported).ToListAsync(cancellationToken));
        Assert.Empty(await verify.AnalysisStatuses.Where(s => s.ItemId == imported).ToListAsync(cancellationToken));

        Assert.Single(await verify.ChapterAnalysisResults.Where(r => r.ItemId == native).ToListAsync(cancellationToken));
        Assert.Single(await verify.ChromaprintResults.Where(r => r.ItemId == native).ToListAsync(cancellationToken));
        Assert.Single(await verify.AnalysisStatuses.Where(s => s.ItemId == native).ToListAsync(cancellationToken));
    }

    /// <summary>
    /// An install that never ran the import must come through the migration with everything intact.
    /// </summary>
    [Fact]
    public async Task CleanupLeavesAnUnimportedDatabaseAlone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var native = Guid.NewGuid();

        await using (var db = new SegmentDbContext(_options))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeCleanup, cancellationToken);

            db.ChapterAnalysisResults.Add(NewChapterRow(native, "chromaprint"));
            db.ChromaprintResults.Add(NewFingerprint(native));
            db.AnalysisStatuses.Add(NewStatus(native, ProviderNames.Chromaprint));

            await db.SaveChangesAsync(cancellationToken);
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
        }

        await using var verify = new SegmentDbContext(_options);

        Assert.Single(await verify.ChapterAnalysisResults.ToListAsync(cancellationToken));
        Assert.Single(await verify.ChromaprintResults.ToListAsync(cancellationToken));
        Assert.Single(await verify.AnalysisStatuses.ToListAsync(cancellationToken));
    }

    private static ChapterAnalysisResult NewChapterRow(Guid itemId, string matchedChapterName) => new()
    {
        ItemId = itemId,
        MatchedChapterName = matchedChapterName,
        SegmentType = 5,
        StartTicks = 0,
        EndTicks = TimeSpan.FromSeconds(60).Ticks,
        CreatedAt = DateTime.UtcNow
    };

    private static ChromaprintResult NewFingerprint(Guid itemId) => new()
    {
        ItemId = itemId,
        Region = SegmentSourceNames.RegionIntro,
        SeasonId = Guid.NewGuid(),
        FingerprintData = [1, 2, 3, 4],
        AnalysisDurationSeconds = 600,
        CreatedAt = DateTime.UtcNow
    };

    private static AnalysisStatus NewStatus(Guid itemId, string providerName) => new()
    {
        ItemId = itemId,
        ProviderName = providerName,
        AnalyzedAt = DateTime.UtcNow,
        HasResults = true
    };
}
