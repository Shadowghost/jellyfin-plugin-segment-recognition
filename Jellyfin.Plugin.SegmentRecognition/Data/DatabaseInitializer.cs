using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SegmentRecognition.Data;

/// <summary>
/// Ensures the segment recognition database is created on startup.
/// </summary>
public class DatabaseInitializer : IHostedService
{
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInitializer"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    public DatabaseInitializer(IDbContextFactory<SegmentDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        // Write-ahead logging, set once on the database file (it is persistent, not per-connection).
        // The analysis task writes from several workers in parallel while the segment providers
        // read on the playback path; under the default rollback journal every one of those readers
        // blocks behind a writer and vice versa, which surfaces as "database is locked". WAL lets
        // readers proceed during a write. NORMAL synchronous is the standard companion setting for
        // WAL: durability is only at risk on OS/power failure, and this database is a rebuildable
        // cache.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
