using System;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Response body for <c>DELETE /SegmentRecognition/Items/{itemId}</c>.
/// </summary>
public class DeleteResultDto
{
    /// <summary>
    /// Gets or sets the item identifier whose data was removed.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the number of rows removed (sum across all per-provider tables).
    /// </summary>
    public int RowsRemoved { get; set; }
}
