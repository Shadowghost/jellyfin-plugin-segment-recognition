using System;
using System.Threading;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using MediaBrowser.Controller.Chapters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Snaps segment boundaries to nearby chapter markers.
/// Chapter markers are placed by content creators and are typically more precise
/// than algorithmically detected boundaries.
/// </summary>
public class ChapterSnapper
{
    private readonly IChapterManager _chapterManager;
    private readonly ILogger<ChapterSnapper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterSnapper"/> class.
    /// </summary>
    /// <param name="chapterManager">The chapter manager.</param>
    /// <param name="logger">The logger.</param>
    public ChapterSnapper(
        IChapterManager chapterManager,
        ILogger<ChapterSnapper> logger)
    {
        _chapterManager = chapterManager;
        _logger = logger;
    }

    /// <summary>
    /// Snaps a target tick value to the nearest chapter boundary within the configured window.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="targetTicks">The target position in ticks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapped tick value, or the original target if no chapter is found within the window.</returns>
    public long SnapToChapter(
        Guid itemId,
        long targetTicks,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableChapterSnapping)
        {
            return targetTicks;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var chapters = _chapterManager.GetChapters(itemId);
        if (chapters.Count == 0)
        {
            return targetTicks;
        }

        var maxWindowTicks = (long)(config.ChapterSnapWindowSeconds * TimeSpan.TicksPerSecond);
        long? bestTicks = null;
        long bestDistance = long.MaxValue;

        foreach (var chapter in chapters)
        {
            var distance = Math.Abs(chapter.StartPositionTicks - targetTicks);
            if (distance <= maxWindowTicks && distance < bestDistance)
            {
                bestDistance = distance;
                bestTicks = chapter.StartPositionTicks;
            }
        }

        if (bestTicks.HasValue)
        {
            _logger.LogDebug(
                "Chapter snap: {Original} -> {Snapped} (delta {DeltaMs}ms) for item {ItemId}",
                targetTicks,
                bestTicks.Value,
                bestDistance / TimeSpan.TicksPerMillisecond,
                itemId);
            return bestTicks.Value;
        }

        return targetTicks;
    }
}
