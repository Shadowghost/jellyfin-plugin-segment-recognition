namespace Jellyfin.Plugin.SegmentRecognition;

using System;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Extensions.Logging;

/// <summary>
/// Update EDL files associated with a list of episodes.
/// </summary>
public static class EdlManager
{
    private static ILogger? _logger;

    /// <summary>
    /// Initialize EDLManager with a logger.
    /// </summary>
    /// <param name="logger">ILogger.</param>
    public static void Initialize(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Logs the configuration that will be used during EDL file creation.
    /// </summary>
    public static void LogConfiguration()
    {
        if (_logger is null)
        {
            throw new InvalidOperationException("Logger must not be null");
        }

        var config = Plugin.Instance!.Configuration;

        if (config.EdlAction == EdlAction.None)
        {
            _logger.LogDebug("EDL action: None - taking no further action");
            return;
        }

        _logger.LogDebug("EDL action: {Action}", config.EdlAction);
        _logger.LogDebug("Regenerate EDL files: {Regenerate}", config.RegenerateEdlFiles);
    }

    /// <summary>
    /// If the EDL action is set to a value other than None, update EDL files for the provided episodes.
    /// </summary>
    /// <param name="episodes">Episodes to update EDL files for.</param>
    public static void UpdateEDLFiles(ReadOnlyCollection<QueuedEpisode> episodes)
    {
        var regenerate = Plugin.Instance!.Configuration.RegenerateEdlFiles;
        var action = Plugin.Instance!.Configuration.EdlAction;
        if (action == EdlAction.None)
        {
            _logger?.LogDebug("EDL action is set to none, not updating EDL files");
            return;
        }

        _logger?.LogDebug("Updating EDL files with action {Action}", action);

        foreach (var episode in episodes)
        {
            var id = episode.EpisodeId;

            if (!Plugin.Instance!.Intros.TryGetValue(id, out var intro))
            {
                _logger?.LogDebug("Episode {Id} did not have an introduction, skipping", id);
                continue;
            }
            else if (!intro.Valid)
            {
                _logger?.LogDebug("Episode {Id} did not have a valid introduction, skipping", id);
                continue;
            }

            var edlPath = GetEdlPath(Plugin.Instance!.GetItemPath(id));

            _logger?.LogTrace("Episode {Id} has EDL path {Path}", id, edlPath);

            if (!regenerate && File.Exists(edlPath))
            {
                _logger?.LogTrace("Refusing to overwrite existing EDL file {Path}", edlPath);
                continue;
            }

            File.WriteAllText(edlPath, intro.ToEdl(action));
        }
    }

    /// <summary>
    /// Given the path to an episode, return the path to the associated EDL file.
    /// </summary>
    /// <param name="mediaPath">Full path to episode.</param>
    /// <returns>Full path to EDL file.</returns>
    public static string GetEdlPath(string mediaPath)
    {
        return Path.ChangeExtension(mediaPath, "edl");
    }
}
