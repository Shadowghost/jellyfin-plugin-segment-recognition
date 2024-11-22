namespace Jellyfin.Plugin.SegmentRecognition;

using System.Collections.ObjectModel;
using System.Threading;
using Jellyfin.Data.Enums;

/// <summary>
/// Media file analyzer interface.
/// </summary>
public interface IMediaFileAnalyzer
{
    /// <summary>
    /// Analyze media files for shared introductions or credits, returning all media files that were **not successfully analyzed**.
    /// </summary>
    /// <param name="analysisQueue">Collection of not analyzed media files.</param>
    /// <param name="mode">The <see cref="MediaSegmentType"/> to analyze for.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>Collection of media files that were **unsuccessfully analyzed**.</returns>
    public ReadOnlyCollection<QueuedEpisode> AnalyzeMediaFiles(
        ReadOnlyCollection<QueuedEpisode> analysisQueue,
        MediaSegmentType mode,
        CancellationToken cancellationToken);
}
