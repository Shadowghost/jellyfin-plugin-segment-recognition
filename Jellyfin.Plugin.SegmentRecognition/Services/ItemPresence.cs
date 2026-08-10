namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Whether an item has been analyzed and whether any provider stored results for it.
/// </summary>
public enum ItemPresence
{
    /// <summary>No <see cref="Data.Entities.AnalysisStatus"/> rows exist for the item.</summary>
    NotAnalyzed,

    /// <summary>The item was analyzed but no provider produced results.</summary>
    AnalyzedNoResults,

    /// <summary>At least one provider stored results for the item.</summary>
    HasResults,
}
