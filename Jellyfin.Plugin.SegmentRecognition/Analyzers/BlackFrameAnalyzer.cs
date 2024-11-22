namespace Jellyfin.Plugin.SegmentRecognition;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

/// <summary>
/// Media file analyzer used to detect end credits that consist of text overlaid on a black background.
/// Bisects the end of the video file to perform an efficient search.
/// </summary>
public class BlackFrameAnalyzer : IMediaFileAnalyzer
{
    private readonly TimeSpan _maximumError = new(0, 0, 4);

    private readonly ILogger<BlackFrameAnalyzer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlackFrameAnalyzer"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public BlackFrameAnalyzer(ILogger<BlackFrameAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ReadOnlyCollection<QueuedEpisode> AnalyzeMediaFiles(
        ReadOnlyCollection<QueuedEpisode> analysisQueue,
        MediaSegmentType mode,
        CancellationToken cancellationToken)
    {
        if (mode != MediaSegmentType.Outro)
        {
            throw new NotImplementedException("Mode needs to be Outro");
        }

        var creditTimes = new Dictionary<Guid, Intro>();

        foreach (var episode in analysisQueue)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var intro = AnalyzeMediaFile(
                episode,
                mode,
                Plugin.Instance!.Configuration.BlackFrameMinimumPercentage);

            if (intro is null)
            {
                continue;
            }

            creditTimes[episode.EpisodeId] = intro;
        }

        Plugin.Instance!.UpdateTimestamps(creditTimes, mode);

        return analysisQueue
            .Where(x => !creditTimes.ContainsKey(x.EpisodeId))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Analyzes an individual media file. Only public because of unit tests.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <returns>Credits timestamp.</returns>
    public Intro? AnalyzeMediaFile(QueuedEpisode episode, MediaSegmentType mode, int minimum)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        // Start by analyzing the last N minutes of the file.
        var start = TimeSpan.FromSeconds(config.MaximumEpisodeCreditsDuration);
        var end = TimeSpan.FromSeconds(config.MinimumCreditsDuration);
        var firstFrameTime = 0.0;

        // Continue bisecting the end of the file until the range that contains the first black
        // frame is smaller than the maximum permitted error.
        while (start - end > _maximumError)
        {
            // Analyze the middle two seconds from the current bisected range
            var midpoint = (start + end) / 2;
            var scanTime = episode.Duration - midpoint.TotalSeconds;
            var tr = new TimeRange(scanTime, scanTime + 2);

            _logger.LogTrace(
                "{Episode}, dur {Duration}, bisect [{BStart}, {BEnd}], time [{Start}, {End}]",
                episode.Name,
                episode.Duration,
                start,
                end,
                tr.Start,
                tr.End);

            var frames = FFmpegWrapper.DetectBlackFrames(episode, tr, minimum);
            _logger.LogTrace(
                "{Episode} at {Start} has {Count} black frames",
                episode.Name,
                tr.Start,
                frames.Length);

            if (frames.Length == 0)
            {
                // Since no black frames were found, slide the range closer to the end
                start = midpoint - TimeSpan.FromSeconds(2);
            }
            else
            {
                // Some black frames were found, slide the range closer to the start
                end = midpoint;
                firstFrameTime = frames[0].Time + scanTime;
            }
        }

        if (firstFrameTime > 0)
        {
            return new(episode.EpisodeId, new TimeRange(firstFrameTime, episode.Duration));
        }

        return null;
    }
}
