using System;
using System.Linq;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Storage;

/// <summary>
/// Tests for the stored fingerprint region offset.
/// </summary>
/// <remarks>
/// <para>
/// Absolute credits positions were re-derived as <c>runtime - AnalysisDurationSeconds</c>. That is
/// wrong twice over: the credits region is anchored to the <em>audio</em> duration when
/// ProbeAudioDuration is enabled (which can be materially shorter than a container runtime
/// inflated by a subtitle track), and the stored duration is truncated to whole seconds. Every
/// credits segment was shifted by the difference.
/// </para>
/// <para>
/// These tests pin the arithmetic that <c>ChromaprintProvider</c> applies, including the fallback
/// that keeps rows written before the column existed working.
/// </para>
/// </remarks>
public sealed class ChromaprintRegionOffsetTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    /// <summary>Mirrors the provider's offset resolution.</summary>
    private static long ResolveOffset(ChromaprintResult row, long runtimeTicks, bool isCredits)
    {
        var offset = row.RegionStartTicks;
        if (isCredits && offset == 0)
        {
            offset = Math.Max(0, runtimeTicks - (row.AnalysisDurationSeconds * Second));
        }

        return offset;
    }

    private static ChromaprintResult Row(long regionStartTicks, int analysisSeconds, string region) => new()
    {
        ItemId = Guid.NewGuid(),
        Region = region,
        SeasonId = Guid.NewGuid(),
        FingerprintData = [1, 2, 3, 4],
        AnalysisDurationSeconds = analysisSeconds,
        RegionStartTicks = regionStartTicks,
        ConfigHash = "hash",
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void RegionStartTicks_RoundTripsThroughStorage()
    {
        using var fixture = new SegmentDbFixture();
        var row = Row(1_260 * Second, 240, "Credits");

        using (var db = fixture.CreateContext())
        {
            db.ChromaprintResults.Add(row);
            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            Assert.Equal(1_260 * Second, db.ChromaprintResults.Single().RegionStartTicks);
        }
    }

    /// <summary>
    /// The case the column exists for: an MKV whose container runtime (1500s) exceeds its audio
    /// duration (1481s). The region starts at 1241s, not at 1500 - 240 = 1260s.
    /// </summary>
    [Fact]
    public void StoredOffsetBeatsRuntimeDerivation_WhenAudioIsShorterThanContainer()
    {
        const long runtimeTicks = 1500 * Second;
        var row = Row(regionStartTicks: 1241 * Second, analysisSeconds: 240, "Credits");

        var offset = ResolveOffset(row, runtimeTicks, isCredits: true);

        Assert.Equal(1241 * Second, offset);

        // The old derivation would have placed the segment 19s late.
        var legacy = runtimeTicks - (row.AnalysisDurationSeconds * Second);
        Assert.Equal(19 * Second, legacy - offset);
    }

    /// <summary>
    /// Truncation alone was worth up to a second of drift even without audio-duration probing.
    /// </summary>
    [Fact]
    public void StoredOffsetAvoidsSecondTruncationDrift()
    {
        const long runtimeTicks = 1500 * Second;

        // Region really starts at 1259.6s; the int duration column rounds 240.4 down to 240.
        var row = Row(regionStartTicks: (long)(1259.6 * Second), analysisSeconds: 240, "Credits");

        var offset = ResolveOffset(row, runtimeTicks, isCredits: true);
        var legacy = runtimeTicks - (row.AnalysisDurationSeconds * Second);

        Assert.NotEqual(legacy, offset);
        Assert.Equal((long)(1259.6 * Second), offset);
    }

    /// <summary>
    /// Rows written before the column existed carry 0. For credits that is not a real value - a
    /// credits fingerprint never starts at the file start - so the old derivation is used.
    /// </summary>
    [Fact]
    public void LegacyCreditsRow_FallsBackToRuntimeDerivation()
    {
        const long runtimeTicks = 1500 * Second;
        var row = Row(regionStartTicks: 0, analysisSeconds: 240, "Credits");

        Assert.Equal(1260 * Second, ResolveOffset(row, runtimeTicks, isCredits: true));
    }

    /// <summary>
    /// An intro region legitimately starts at 0, so the fallback must never apply to it.
    /// </summary>
    [Fact]
    public void IntroRegion_KeepsZeroOffset()
    {
        var row = Row(regionStartTicks: 0, analysisSeconds: 600, "Intro");

        Assert.Equal(0, ResolveOffset(row, 1500 * Second, isCredits: false));
    }

    [Fact]
    public void DefaultOffsetIsZeroForNewRows()
    {
        using var fixture = new SegmentDbFixture();

        using (var db = fixture.CreateContext())
        {
            db.ChromaprintResults.Add(new ChromaprintResult
            {
                ItemId = Guid.NewGuid(),
                Region = "Intro",
                SeasonId = Guid.NewGuid(),
                FingerprintData = [],
                AnalysisDurationSeconds = 0,
                ConfigHash = "hash",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            Assert.Equal(0, db.ChromaprintResults.Single().RegionStartTicks);
        }
    }
}
