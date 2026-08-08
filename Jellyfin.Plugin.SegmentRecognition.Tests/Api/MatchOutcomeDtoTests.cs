using Jellyfin.Plugin.SegmentRecognition.Api.Mappers;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Api;

/// <summary>
/// Tests for exposing the per-region match outcome on the wire.
/// </summary>
public sealed class MatchOutcomeDtoTests
{
    [Fact]
    public void MapperRendersOutcomeAsItsName()
    {
        Assert.Equal("NoSharedAudio", SegmentDtoMapper.OutcomeName(SegmentMatchOutcome.NoSharedAudio));
        Assert.Equal("SeasonOutlier", SegmentDtoMapper.OutcomeName(SegmentMatchOutcome.SeasonOutlier));
        Assert.Null(SegmentDtoMapper.OutcomeName(null));
        Assert.Null(SegmentDtoMapper.OutcomeName((SegmentMatchOutcome)999));
    }

    /// <summary>
    /// "No intro" must not be reported through the error channel: the controller exposes a
    /// hasError filter for genuine provider failures, and an episode that simply has nothing to
    /// match is not a failure.
    /// </summary>
    [Fact]
    public void OutcomeIsSeparateFromLastError()
    {
        var dto = SegmentDtoMapper.ToDto(new AnalysisStatus
        {
            ProviderName = "Chromaprint",
            HasResults = false,
            IntroOutcome = SegmentMatchOutcome.NoSharedAudio,
        });

        Assert.Equal("NoSharedAudio", dto.IntroOutcome);
        Assert.Null(dto.LastError);
        Assert.Null(dto.LastErrorAt);
    }
}
