using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Server entrypoint.
/// </summary>
public class SegmentRecognitionManager : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SegmentRecognitionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentRecognitionManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public SegmentRecognitionManager(
        ILibraryManager libraryManager,
        ILogger<SegmentRecognitionManager> logger,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Registers event handler.
    /// </summary>
    /// <returns>Task.</returns>
    public Task RunAsync()
    {
        FFmpegWrapper.Logger = _logger;

        // TODO: when a new item is added to the server, immediately analyze the season it belongs to
        // instead of waiting for the next task interval. The task start should be debounced by a few seconds.

        try
        {
            // Enqueue all episodes at startup to ensure any FFmpeg errors appear as early as possible
            _logger.LogInformation("Running startup enqueue");
            var queueManager = new QueueManager(_loggerFactory.CreateLogger<QueueManager>(), _libraryManager);
            queueManager.GetMediaItems();
        }
        catch (Exception ex)
        {
            _logger.LogError("Unable to run startup enqueue: {Exception}", ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
