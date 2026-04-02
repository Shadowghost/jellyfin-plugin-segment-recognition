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
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
