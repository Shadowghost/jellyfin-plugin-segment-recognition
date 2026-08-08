using System;
using System.Data.Common;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;

/// <summary>
/// Backs a <see cref="SegmentDbContext"/> with a real in-memory SQLite database.
/// </summary>
/// <remarks>
/// A real SQLite engine rather than the EF in-memory provider: the behaviour under test is
/// largely storage-level (unique indexes, <c>ExecuteDelete</c>, autoincrement keys), none of which
/// the in-memory provider enforces. The connection is held open for the fixture's lifetime because
/// an in-memory database is destroyed when its last connection closes.
/// </remarks>
public sealed class SegmentDbFixture : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<SegmentDbContext> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentDbFixture"/> class.
    /// </summary>
    public SegmentDbFixture()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<SegmentDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    /// <summary>
    /// Gets a factory that hands out contexts over the shared in-memory database.
    /// </summary>
    public IDbContextFactory<SegmentDbContext> Factory => new PooledFactory(_options);

    /// <summary>
    /// Creates a new context over the shared in-memory database.
    /// </summary>
    /// <returns>The context.</returns>
    public SegmentDbContext CreateContext() => new(_options);

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }

    private sealed class PooledFactory : IDbContextFactory<SegmentDbContext>
    {
        private readonly DbContextOptions<SegmentDbContext> _options;

        public PooledFactory(DbContextOptions<SegmentDbContext> options) => _options = options;

        public SegmentDbContext CreateDbContext() => new(_options);
    }
}
