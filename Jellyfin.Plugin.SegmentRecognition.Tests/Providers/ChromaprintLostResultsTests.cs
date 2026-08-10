using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests that a group comparison reports the items whose segments it removed.
/// </summary>
/// <remarks>
/// Removing a segment from the plugin's cache does not remove it from Jellyfin - only a push does
/// that, and the push is what an item losing its results no longer qualifies for. Unless the
/// comparison names those items, Jellyfin keeps serving a segment the plugin has already
/// discarded, and no amount of re-running the task fixes it.
/// </remarks>
[Collection(PluginStateCollection.Name)]
public sealed class ChromaprintLostResultsTests : IDisposable
{
    private const long Second = TimeSpan.TicksPerSecond;

    private readonly SegmentDbFixture _fixture = new();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IMediaSourceManager _mediaSourceManager = Substitute.For<IMediaSourceManager>();
    private readonly IChapterManager _chapterManager = Substitute.For<IChapterManager>();
    private readonly IKeyframeManager _keyframeManager = Substitute.For<IKeyframeManager>();
    private readonly Guid _seasonId = Guid.NewGuid();

    public void Dispose() => _fixture.Dispose();

    private ChromaprintProvider CreateProvider()
    {
        var chromaprint = Substitute.ForPartsOf<FfmpegChromaprintService>(
            Substitute.For<IMediaEncoder>(),
            NullLogger<FfmpegChromaprintService>.Instance);

        var blackFrame = Substitute.ForPartsOf<FfmpegBlackFrameService>(
            Substitute.For<IMediaEncoder>(),
            Substitute.For<IConfigurationManager>(),
            NullLogger<FfmpegBlackFrameService>.Instance);

        var pipeline = new RefinementPipeline(
            new SegmentRefiner(blackFrame, NullLogger<SegmentRefiner>.Instance),
            new ChapterSnapper(_chapterManager, NullLogger<ChapterSnapper>.Instance),
            new KeyframeSnapper(_keyframeManager, NullLogger<KeyframeSnapper>.Instance));

        return new ChromaprintProvider(
            chromaprint,
            _libraryManager,
            _mediaSourceManager,
            _fixture.Factory,
            pipeline,
            NullLogger<ChromaprintProvider>.Instance);
    }

    /// <summary>
    /// Registers an episode with the library and gives it a fingerprint that matches nothing:
    /// deterministic per-item bytes, so the comparison cannot find a shared region.
    /// </summary>
    private async Task<Guid> GivenEpisodeWithUnmatchableFingerprintAsync(int index)
    {
        var id = Guid.NewGuid();
        var episode = new Episode
        {
            Id = id,
            Name = $"E{index}",
            Path = $"/media/e{index}.mkv",
            IndexNumber = index,
            SeasonId = _seasonId,
            RunTimeTicks = 1400 * Second,
        };
        _libraryManager.GetItemById(id).Returns(episode);
        _mediaSourceManager.GetMediaStreams(id).Returns(new List<MediaStream>());

        var data = new byte[4000];
        new Random(index).NextBytes(data);

        using var db = _fixture.Factory.CreateDbContext();
        db.ChromaprintResults.Add(new ChromaprintResult
        {
            ItemId = id,
            Region = SegmentSourceNames.RegionIntro,
            SeasonId = _seasonId,
            FingerprintData = data,
            AnalysisDurationSeconds = 350,
            RegionStartTicks = 0,
            ConfigHash = "fp",
            CreatedAt = DateTime.UtcNow,
        });

        // A segment from an earlier run, stored under a configuration that no longer applies, so
        // this run re-evaluates it rather than leaving it alone.
        db.ChapterAnalysisResults.Add(new ChapterAnalysisResult
        {
            ItemId = id,
            SegmentType = (int)MediaSegmentType.Intro,
            StartTicks = 30 * Second,
            EndTicks = 120 * Second,
            MatchedChapterName = SegmentSourceNames.ChromaprintIntro,
            ConfigHash = "stale",
            CreatedAt = DateTime.UtcNow,
        });

        db.AnalysisStatuses.Add(new AnalysisStatus
        {
            ItemId = id,
            ProviderName = ProviderNames.Chromaprint,
            AnalyzedAt = DateTime.UtcNow,
            HasResults = true,
            ConfigHash = "stale",
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>
    /// A rematch that finds nothing must clear what the previous run stored, flip the status, and
    /// say why. The status row surviving with <c>HasResults=false</c> is what later lets the task
    /// push the item and clear Jellyfin's copy - a deleted status row would strand it.
    /// </summary>
    [Fact]
    public async Task FailedRematchClearsStoredSegmentsAndRecordsWhy()
    {
        using var _ = new PluginConfigScope();

        var ids = new List<Guid>();
        for (var i = 1; i <= 4; i++)
        {
            ids.Add(await GivenEpisodeWithUnmatchableFingerprintAsync(i));
        }

        await CreateProvider().AnalyzeGroupAsync(_seasonId, CancellationToken.None);

        using var db = _fixture.Factory.CreateDbContext();

        Assert.Empty(await db.ChapterAnalysisResults
            .Where(r => r.MatchedChapterName == SegmentSourceNames.ChromaprintIntro)
            .ToListAsync(TestContext.Current.CancellationToken));

        var statuses = await db.AnalysisStatuses.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ids.Count, statuses.Count);
        Assert.All(statuses, s =>
        {
            Assert.False(s.HasResults);
            Assert.Equal(SegmentMatchOutcome.NoSharedAudio, s.IntroOutcome);
        });
    }

    /// <summary>
    /// The same run over items that never had a segment leaves the same recorded state, so the
    /// task's push gate does not need to distinguish the two cases.
    /// </summary>
    [Fact]
    public async Task ItemsThatNeverHadSegmentsEndInTheSameState()
    {
        using var _ = new PluginConfigScope();

        for (var i = 1; i <= 4; i++)
        {
            await GivenEpisodeWithUnmatchableFingerprintAsync(i);
        }

        using (var seed = _fixture.Factory.CreateDbContext())
        {
            await seed.ChapterAnalysisResults.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await seed.AnalysisStatuses.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.HasResults, false),
                TestContext.Current.CancellationToken);
        }

        await CreateProvider().AnalyzeGroupAsync(_seasonId, CancellationToken.None);

        using var db = _fixture.Factory.CreateDbContext();
        Assert.All(
            await db.AnalysisStatuses.ToListAsync(TestContext.Current.CancellationToken),
            s =>
            {
                Assert.False(s.HasResults);
                Assert.Equal(SegmentMatchOutcome.NoSharedAudio, s.IntroOutcome);
            });
    }
}
