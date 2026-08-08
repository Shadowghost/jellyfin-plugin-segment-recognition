namespace Jellyfin.Plugin.SegmentRecognition.Data.Entities;

/// <summary>
/// Why a cross-matching provider did, or did not, produce a segment for one item and region.
/// </summary>
/// <remarks>
/// Absence of a result row is ambiguous on its own: an item that was never analyzed, one whose
/// season has nothing to compare against, and one whose audio simply differs from every sibling
/// all look identical from the outside. Recording the outcome turns "why does this episode have
/// no intro?" into something the API can answer directly.
/// </remarks>
public enum SegmentMatchOutcome
{
    /// <summary>
    /// A region was agreed on and stored.
    /// </summary>
    Matched = 0,

    /// <summary>
    /// Nothing was compared: the group holds no other fingerprint, or every other fingerprint
    /// belongs to another version of the same title.
    /// </summary>
    NoComparableCounterparts = 1,

    /// <summary>
    /// Counterparts were compared but none shared a run of audio reaching the region's minimum
    /// duration. The item's audio genuinely differs from its siblings' - a re-encode or a
    /// differently-cut release sitting in the same folder.
    /// </summary>
    NoSharedAudio = 2,

    /// <summary>
    /// Shared audio was found, but every matched region fell outside the configured duration or
    /// position window for this segment type.
    /// </summary>
    OutsideWindow = 3,

    /// <summary>
    /// In-window regions were found, but too few counterparts agreed on one for the consensus
    /// rule to accept it.
    /// </summary>
    NoConsensus = 4,

    /// <summary>
    /// A region was agreed on, then discarded because it sat at a position almost no other
    /// episode in the season shared. See
    /// <see cref="Providers.ChromaprintProvider.SelectSeasonOutliers"/>.
    /// </summary>
    SeasonOutlier = 5,
}
