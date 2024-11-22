using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Analyze all television episodes for credits.
/// TODO: analyze all media files.
/// </summary>
public class DetectCreditsTask : IScheduledTask
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly BaseItemAnalyzer _baseItemAnalyzer;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectCreditsTask"/> class.
    /// </summary>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">The <see cref="QueueManager"/>.</param>
    /// <param name="chapterAnalyzer">The <see cref="ChapterAnalyzer"/>.</param>
    /// <param name="chromaprintAnalyzer">The <see cref="ChromaprintAnalyzer"/>.</param>
    /// <param name="blackFrameAnalyzer">The <see cref="BlackFrameAnalyzer"/>.</param>
    public DetectCreditsTask(
        ILoggerFactory loggerFactory,
        QueueManager queueManager,
        ChapterAnalyzer chapterAnalyzer,
        ChromaprintAnalyzer chromaprintAnalyzer,
        BlackFrameAnalyzer blackFrameAnalyzer)
    {
        _loggerFactory = loggerFactory;
        _baseItemAnalyzer = new BaseItemAnalyzer(
            [AnalysisMode.Credits],
            queueManager,
            _loggerFactory.CreateLogger<DetectCreditsTask>(),
            chapterAnalyzer,
            chromaprintAnalyzer,
            blackFrameAnalyzer);
    }

    /// <summary>
    /// Gets the task name.
    /// </summary>
    public string Name => "Detect Credits";

    /// <summary>
    /// Gets the task category.
    /// </summary>
    public string Category => "Intro Skipper";

    /// <summary>
    /// Gets the task description.
    /// </summary>
    public string Description => "Analyzes the audio and video of all television episodes to find credits.";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "CPBIntroSkipperDetectCredits";

    /// <summary>
    /// Analyze all episodes in the queue. Only one instance of this task should be run at a time.
    /// </summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _baseItemAnalyzer.AnalyzeItems(progress, cancellationToken);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }
}
