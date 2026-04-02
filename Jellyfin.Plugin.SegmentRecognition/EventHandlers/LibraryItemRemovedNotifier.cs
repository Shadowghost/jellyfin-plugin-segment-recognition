using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Data;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.EventHandlers;

/// <summary>
/// Handles library item removal by cleaning up associated analysis data.
/// </summary>
public sealed class LibraryItemRemovedNotifier : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILogger<LibraryItemRemovedNotifier> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryItemRemovedNotifier"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="logger">The logger.</param>
    public LibraryItemRemovedNotifier(
        ILibraryManager libraryManager,
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILogger<LibraryItemRemovedNotifier> logger)
    {
        _libraryManager = libraryManager;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _libraryManager.ItemRemoved -= OnItemRemoved;
            _disposed = true;
        }
    }

    private async void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            await PruneItemDataAsync(e.Item.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pruning data for removed item {ItemId}", e.Item.Id);
        }
    }

    private async Task PruneItemDataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        db.AnalysisStatuses.RemoveRange(
            db.AnalysisStatuses.Where(s => s.ItemId == itemId));
        db.BlackFrameResults.RemoveRange(
            db.BlackFrameResults.Where(r => r.ItemId == itemId));
        db.CropDetectResults.RemoveRange(
            db.CropDetectResults.Where(r => r.ItemId == itemId));
        db.ChromaprintResults.RemoveRange(
            db.ChromaprintResults.Where(r => r.ItemId == itemId));
        db.ChapterAnalysisResults.RemoveRange(
            db.ChapterAnalysisResults.Where(r => r.ItemId == itemId));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Pruned analysis data for removed item {ItemId}", itemId);
    }
}
