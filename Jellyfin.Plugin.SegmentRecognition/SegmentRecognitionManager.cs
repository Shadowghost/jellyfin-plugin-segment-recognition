using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Server entrypoint.
/// </summary>
public class SegmentRecognitionManager : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<SegmentRecognitionManager> _logger;
    private readonly QueueManager _queueManager;
    private readonly BaseItemAnalyzer _analyzer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentRecognitionManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{SegmentRecognitionManager}"/> interface.</param>
    /// <param name="queueManager">The <see cref="QueueManager"/>.</param>
    /// <param name="chapterAnalyzer">The <see cref="ChapterAnalyzer"/>.</param>
    /// <param name="chromaprintAnalyzer">The <see cref="ChromaprintAnalyzer"/>.</param>
    /// <param name="blackFrameAnalyzer">The <see cref="BlackFrameAnalyzer"/>.</param>
    public SegmentRecognitionManager(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<SegmentRecognitionManager> logger,
        QueueManager queueManager,
        ChapterAnalyzer chapterAnalyzer,
        ChromaprintAnalyzer chromaprintAnalyzer,
        BlackFrameAnalyzer blackFrameAnalyzer)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _queueManager = queueManager;
        _logger = logger;

        _analyzer = new BaseItemAnalyzer(
            [MediaSegmentType.Intro, MediaSegmentType.Outro],
            queueManager,
            logger,
            chapterAnalyzer,
            chromaprintAnalyzer,
            blackFrameAnalyzer);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe to item modification events
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemModified;
        _taskManager.TaskCompleted += OnLibraryRefresh;

        // Set FFmpeg logger
        FFmpegWrapper.Logger = _logger;

        try
        {
            // Enqueue all episodes at startup to ensure any FFmpeg errors appear as early as possible
            _logger.LogInformation("Running startup enqueue");
            _queueManager.GetMediaItems();
        }
        catch (Exception ex)
        {
            _logger.LogError("Unable to run startup enqueue: {Exception}", ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemModified;
        _taskManager.TaskCompleted -= OnLibraryRefresh;

        return Task.CompletedTask;
    }

    private static bool IsItemSupported(BaseItem item)
    {
        // Only episodes and non-virtual items are supported
        return item is not Episode || item.LocationType == LocationType.Virtual;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        if (!IsItemSupported(itemChangeEventArgs.Item))
        {
            return;
        }

        try
        {
            Analyze();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error analyzing: {Exception}", ex);
        }
    }

    private void OnItemModified(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        if (!IsItemSupported(itemChangeEventArgs.Item))
        {
            return;
        }

        try
        {
            Analyze();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error analyzing: {Exception}", ex);
        }
    }

    private void OnLibraryRefresh(object? sender, TaskCompletionEventArgs eventArgs)
    {
        var result = eventArgs.Result;
        if (result.Key != "RefreshLibrary" || result.Status != TaskCompletionStatus.Completed)
        {
            return;
        }

        try
        {
            Analyze();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error analyzing: {Exception}", ex);
        }
    }

    private void Analyze()
    {
        _analyzer.AnalyzeItems(new Progress<double>(), new CancellationToken(false));
    }
}
