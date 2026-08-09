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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests for choosing which intros to search for again over a wider region.
/// </summary>
/// <remarks>
/// An opening past the first-pass region often still matches a brief shared ident at the head of
/// the file, so "found something" is not evidence the search was wide enough.
/// </remarks>
[Collection(PluginStateCollection.Name)]
public sealed class ChromaprintRetryCandidateTests : IDisposable
{
    private const long Second = TimeSpan.TicksPerSecond;

    private readonly SegmentDbFixture _fixture = new();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IMediaSourceManager _mediaSourceManager = Substitute.For<IMediaSourceManager>();
    private readonly Guid _seasonId = Guid.NewGuid();

    public void Dispose() => _fixture.Dispose();

    private ChromaprintProvider CreateProvider()
    {
        var chromaprint = Substitute.ForPartsOf<FfmpegChromaprintService>(
            Substitute.For<IMediaEncoder>(), NullLogger<FfmpegChromaprintService>.Instance);
        var blackFrame = Substitute.ForPartsOf<FfmpegBlackFrameService>(
            Substitute.For<IMediaEncoder>(), Substitute.For<IConfigurationManager>(),
            NullLogger<FfmpegBlackFrameService>.Instance);
        var pipeline = new RefinementPipeline(
            new SegmentRefiner(blackFrame, NullLogger<SegmentRefiner>.Instance),
            new ChapterSnapper(Substitute.For<IChapterManager>(), NullLogger<ChapterSnapper>.Instance),
            new KeyframeSnapper(Substitute.For<IKeyframeManager>(), NullLogger<KeyframeSnapper>.Instance));

        return new ChromaprintProvider(
            chromaprint, _libraryManager, _mediaSourceManager, _fixture.Factory, pipeline,
            NullLogger<ChromaprintProvider>.Instance);
    }

    /// <summary>Adds an episode with a stored intro fingerprint and, optionally, a matched intro.</summary>
    private async Task<Guid> GivenEpisodeAsync(
        int index, double runtimeSeconds, int fingerprintSeconds, double? introLengthSeconds)
    {
        var id = Guid.NewGuid();
        _libraryManager.GetItemById(id).Returns(new Episode
        {
            Id = id,
            Name = $"E{index}",
            Path = $"/media/e{index}.mkv",
            IndexNumber = index,
            SeasonId = _seasonId,
            RunTimeTicks = (long)(runtimeSeconds * Second),
        });
        _mediaSourceManager.GetMediaStreams(id).Returns(new List<MediaStream>());

        using var db = _fixture.Factory.CreateDbContext();
        db.ChromaprintResults.Add(new ChromaprintResult
        {
            ItemId = id,
            Region = SegmentSourceNames.RegionIntro,
            SeasonId = _seasonId,
            FingerprintData = new byte[fingerprintSeconds * 8],
            AnalysisDurationSeconds = fingerprintSeconds,
            RegionStartTicks = 0,
            ConfigHash = "fp",
            CreatedAt = DateTime.UtcNow,
        });

        if (introLengthSeconds is { } len)
        {
            db.ChapterAnalysisResults.Add(new ChapterAnalysisResult
            {
                ItemId = id,
                SegmentType = (int)MediaSegmentType.Intro,
                StartTicks = 0,
                EndTicks = (long)(len * Second),
                MatchedChapterName = SegmentSourceNames.ChromaprintIntro,
                ConfigHash = "cmp",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>The SNW shape: most episodes match the real opening, one matched a short ident.</summary>
    [Fact]
    public async Task ShortIntroAgainstTheSeasonsBest_IsRetried()
    {
        using var _ = new PluginConfigScope();

        var good1 = await GivenEpisodeAsync(1, 3600, 600, 106);
        var good2 = await GivenEpisodeAsync(2, 3600, 600, 106);
        var suspect = await GivenEpisodeAsync(3, 3600, 600, 33);

        var candidates = await CreateProvider().GetIntroRetryCandidatesAsync(_seasonId, CancellationToken.None);

        var only = Assert.Single(candidates);
        Assert.Equal(suspect, only.ItemId);
        Assert.Equal(1800, only.RegionSeconds, 3);
        Assert.DoesNotContain(candidates, c => c.ItemId == good1 || c.ItemId == good2);
    }

    [Fact]
    public async Task ItemWithNoIntro_IsRetried()
    {
        using var _ = new PluginConfigScope();

        await GivenEpisodeAsync(1, 3600, 600, 106);
        await GivenEpisodeAsync(2, 3600, 600, 106);
        var missing = await GivenEpisodeAsync(3, 3600, 600, null);

        var candidates = await CreateProvider().GetIntroRetryCandidatesAsync(_seasonId, CancellationToken.None);

        Assert.Equal(missing, Assert.Single(candidates).ItemId);
    }

    /// <summary>
    /// Without this the same season would be re-fingerprinted on every run, since a show with no
    /// shared opening never stops looking like a failure.
    /// </summary>
    [Fact]
    public async Task ItemsAlreadyAtTheRetryWidth_AreNotOfferedAgain()
    {
        using var _ = new PluginConfigScope();

        await GivenEpisodeAsync(1, 3600, 600, 106);
        await GivenEpisodeAsync(2, 3600, 600, 106);
        await GivenEpisodeAsync(3, 3600, 1800, null);

        Assert.Empty(await CreateProvider().GetIntroRetryCandidatesAsync(_seasonId, CancellationToken.None));
    }

    /// <summary>A season where everything agrees has nothing to retry.</summary>
    [Fact]
    public async Task ConsistentSeason_IsLeftAlone()
    {
        using var _ = new PluginConfigScope();

        for (var i = 1; i <= 4; i++)
        {
            await GivenEpisodeAsync(i, 3600, 600, 106);
        }

        Assert.Empty(await CreateProvider().GetIntroRetryCandidatesAsync(_seasonId, CancellationToken.None));
    }

    /// <summary>
    /// Short media is fingerprinted whole, so the retry width is already covered and there is
    /// nothing wider to try.
    /// </summary>
    [Fact]
    public async Task ShortMedia_IsNotRetried()
    {
        using var _ = new PluginConfigScope();

        await GivenEpisodeAsync(1, 500, 500, 60);
        await GivenEpisodeAsync(2, 500, 500, null);

        Assert.Empty(await CreateProvider().GetIntroRetryCandidatesAsync(_seasonId, CancellationToken.None));
    }
}
