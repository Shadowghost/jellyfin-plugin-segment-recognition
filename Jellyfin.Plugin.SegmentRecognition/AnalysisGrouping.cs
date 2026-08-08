using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Computes the container (rollup) identifier for an analyzed item. Episodes roll up to their
/// series, alternate versions of a grouped video roll up to their primary version; everything
/// else is its own container. This is the value stored in
/// <see cref="Data.Entities.AnalysisStatus.ContainerId"/> and the key the analyzed-items
/// listing groups by, so write sites and the query must agree on it.
/// </summary>
public static class AnalysisGrouping
{
    /// <summary>
    /// Returns the container id for an item: the series id for an episode (when resolvable),
    /// the primary version id for an alternate version of a grouped video, otherwise the
    /// item's own id.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The container id.</returns>
    public static Guid GetContainerId(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is Episode ep)
        {
            // The persisted SeriesId column can legitimately be empty (e.g. rows written by
            // older library-db migrations); mirror Episode.Series and fall back to the
            // ancestor walk before treating the episode as its own container.
            var seriesId = ep.SeriesId;
            if (seriesId == Guid.Empty)
            {
                seriesId = ep.FindSeriesId();
            }

            if (seriesId != Guid.Empty)
            {
                return seriesId;
            }
        }

        // Alternate versions of a grouped video (4K/1080p, director's cut, …) roll up to the
        // primary version so the listing shows one entry per title instead of one per file.
        if (item is Video { PrimaryVersionId: { } primaryId } && primaryId != Guid.Empty)
        {
            return primaryId;
        }

        return item.Id;
    }

    /// <summary>
    /// Null-tolerant variant of <see cref="GetContainerId(BaseItem)"/>: when the item can't be
    /// resolved from the library, falls back to the supplied item id so the row still gets a
    /// non-empty container rather than being left awaiting backfill forever.
    /// </summary>
    /// <param name="item">The item, or <c>null</c> when it couldn't be resolved.</param>
    /// <param name="fallbackItemId">The item id to use when <paramref name="item"/> is <c>null</c>.</param>
    /// <returns>The container id.</returns>
    public static Guid GetContainerId(BaseItem? item, Guid fallbackItemId)
        => item is null ? fallbackItemId : GetContainerId(item);
}
