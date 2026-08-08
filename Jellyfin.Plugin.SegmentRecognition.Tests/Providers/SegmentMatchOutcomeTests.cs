using System;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests for the reason recorded when cross-matching does or does not produce a segment.
/// </summary>
/// <remarks>
/// Without this, an item that shares no audio with its siblings is indistinguishable from one that
/// was never analyzed: both simply have no result row.
/// </remarks>
public sealed class SegmentMatchOutcomeTests
{
    [Fact]
    public void ConsensusReached_IsMatched()
    {
        Assert.Equal(
            SegmentMatchOutcome.Matched,
            ChromaprintProvider.ClassifyOutcome(hasConsensus: true, counterpartsCompared: 4, anySharedRegion: true, candidateCount: 3));
    }

    /// <summary>
    /// A season holding one episode, or one whose other fingerprints are all alternate versions
    /// of the same title, compares nothing at all.
    /// </summary>
    [Fact]
    public void NothingCompared_IsNoComparableCounterparts()
    {
        Assert.Equal(
            SegmentMatchOutcome.NoComparableCounterparts,
            ChromaprintProvider.ClassifyOutcome(hasConsensus: false, counterpartsCompared: 0, anySharedRegion: false, candidateCount: 0));
    }

    /// <summary>
    /// The Alias case: counterparts were compared and none of them shared any audio, because the
    /// file is a differently-cut release sitting in the same folder.
    /// </summary>
    [Fact]
    public void ComparedButNothingShared_IsNoSharedAudio()
    {
        Assert.Equal(
            SegmentMatchOutcome.NoSharedAudio,
            ChromaprintProvider.ClassifyOutcome(hasConsensus: false, counterpartsCompared: 8, anySharedRegion: false, candidateCount: 0));
    }

    /// <summary>
    /// Shared audio existed but every region was rejected by the duration or position window -
    /// a different problem from having nothing in common, and one the user can fix by widening
    /// the configured window.
    /// </summary>
    [Fact]
    public void SharedButFiltered_IsOutsideWindow()
    {
        Assert.Equal(
            SegmentMatchOutcome.OutsideWindow,
            ChromaprintProvider.ClassifyOutcome(hasConsensus: false, counterpartsCompared: 8, anySharedRegion: true, candidateCount: 0));
    }

    /// <summary>
    /// In-window candidates existed but too few counterparts agreed on one.
    /// </summary>
    [Fact]
    public void CandidatesWithoutAgreement_IsNoConsensus()
    {
        Assert.Equal(
            SegmentMatchOutcome.NoConsensus,
            ChromaprintProvider.ClassifyOutcome(hasConsensus: false, counterpartsCompared: 8, anySharedRegion: true, candidateCount: 1));
    }

    /// <summary>
    /// Every outcome must be a defined member, since the value is persisted as an integer and
    /// read back by name.
    /// </summary>
    [Fact]
    public void EveryOutcomeIsADefinedMember()
    {
        foreach (var outcome in Enum.GetValues<SegmentMatchOutcome>())
        {
            Assert.True(Enum.IsDefined(outcome));
        }
    }
}
