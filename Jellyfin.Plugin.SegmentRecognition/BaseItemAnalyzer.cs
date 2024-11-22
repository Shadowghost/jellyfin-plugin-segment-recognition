namespace Jellyfin.Plugin.SegmentRecognition;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Base item analyzer.
/// </summary>
public class BaseItemAnalyzer
{
    private readonly IReadOnlyList<AnalysisMode> _analysisModes;
    private readonly QueueManager _queueManager;
    private readonly ILogger _logger;
    private readonly ChapterAnalyzer _chapterAnalyzer;
    private readonly ChromaprintAnalyzer _chromaprintAnalyzer;
    private readonly BlackFrameAnalyzer _blackFrameAnalyzer;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseItemAnalyzer"/> class.
    /// </summary>
    /// <param name="modes">Analysis modes.</param>
    /// <param name="queueManager">The <see cref="QueueManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="chapterAnalyzer">The <see cref="ChapterAnalyzer"/>.</param>
    /// <param name="chromaprintAnalyzer">The <see cref="ChromaprintAnalyzer"/>.</param>
    /// <param name="blackFrameAnalyzer">The <see cref="BlackFrameAnalyzer"/>.</param>
    public BaseItemAnalyzer(
        IReadOnlyList<AnalysisMode> modes,
        QueueManager queueManager,
        ILogger logger,
        ChapterAnalyzer chapterAnalyzer,
        ChromaprintAnalyzer chromaprintAnalyzer,
        BlackFrameAnalyzer blackFrameAnalyzer)
    {
        _analysisModes = modes;
        _queueManager = queueManager;
        _logger = logger;
        _chapterAnalyzer = chapterAnalyzer;
        _chromaprintAnalyzer = chromaprintAnalyzer;
        _blackFrameAnalyzer = blackFrameAnalyzer;
    }

    /// <summary>
    /// Analyze all media items on the server.
    /// </summary>
    /// <param name="progress">The Progress.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    public void AnalyzeItems(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var queue = _queueManager.GetMediaItems();

        var totalQueued = 0;
        foreach (var kvp in queue)
        {
            totalQueued += kvp.Value.Count;
        }

        if (totalQueued == 0)
        {
            throw new FingerprintException(
                "No episodes to analyze. If you are limiting the list of libraries to analyze, check that all library names have been spelled correctly.");
        }

        var totalProcessed = 0;
        var modeCount = _analysisModes.Count;
        var options = new ParallelOptions()
        {
            MaxDegreeOfParallelism = Plugin.Instance!.Configuration.MaxParallelism
        };

        Parallel.ForEach(queue, options, (season) =>
        {
            // Since the first run of the task can run for multiple hours, ensure that none
            // of the current media items were deleted from Jellyfin since the task was started.
            var (episodes, modesToExecute) = _queueManager.VerifyQueue(
                season.Value.AsReadOnly(),
                _analysisModes);

            var episodeCount = episodes.Count;
            if (episodeCount == 0)
            {
                return;
            }

            var first = episodes[0];

            if (modesToExecute.Count == 0)
            {
                _logger.LogDebug(
                    "All episodes in {Name} season {Season} have already been analyzed",
                    first.SeriesName,
                    first.SeasonNumber);

                Interlocked.Add(ref totalProcessed, episodeCount);
                progress.Report(totalProcessed * 100 / totalQueued);

                return;
            }

            if (modeCount != modesToExecute.Count)
            {
                Interlocked.Add(ref totalProcessed, episodeCount);
                progress.Report(totalProcessed * 100 / totalQueued);
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                foreach (AnalysisMode mode in modesToExecute)
                {
                    var analyzed = AnalyzeItems(episodes, mode, cancellationToken);
                    Interlocked.Add(ref totalProcessed, analyzed);
                    progress.Report(totalProcessed * 100 / totalQueued);
                }
            }
            catch (FingerprintException ex)
            {
                _logger.LogWarning(
                    "Unable to analyze {Series} season {Season}: unable to fingerprint: {Ex}",
                    first.SeriesName,
                    first.SeasonNumber,
                    ex);
            }

            progress.Report(totalProcessed * 100 / totalQueued);
        });
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments.
    /// </summary>
    /// <param name="items">Media items to analyze.</param>
    /// <param name="mode">The <see cref="AnalysisMode"/>.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>Number of items that were successfully analyzed.</returns>
    private int AnalyzeItems(
        ReadOnlyCollection<QueuedEpisode> items,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var totalItems = items.Count;

        // Only analyze specials (season 0) if the user has opted in.
        var first = items[0];
        if (first.SeasonNumber == 0 && !Plugin.Instance!.Configuration.AnalyzeSeasonZero)
        {
            return 0;
        }

        _logger.LogInformation(
            "Analyzing {Count} files from {Name} season {Season}",
            items.Count,
            first.SeriesName,
            first.SeasonNumber);

        var analyzers = new List<IMediaFileAnalyzer>
        {
            _chapterAnalyzer,
            _chromaprintAnalyzer
        };

        if (mode == AnalysisMode.Credits)
        {
            analyzers.Add(_blackFrameAnalyzer);
        }

        // Use each analyzer to find skippable ranges in all media files, removing successfully
        // analyzed items from the queue.
        foreach (var analyzer in analyzers)
        {
            items = analyzer.AnalyzeMediaFiles(items, mode, cancellationToken);
        }

        return totalItems;
    }
}
