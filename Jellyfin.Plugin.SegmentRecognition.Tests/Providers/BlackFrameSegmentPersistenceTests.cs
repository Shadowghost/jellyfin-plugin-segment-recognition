using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.MediaEncoding.Keyframes;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests that black-frame analysis persists the boundaries it refined, and that the serving path
/// hands those back.
/// </summary>
/// <remarks>
/// Previously the provider ran the whole refinement pipeline and then threw the result away: only
/// a boolean "has results" survived, and <c>GetMediaSegments</c> re-derived unrefined boundaries
/// by re-clustering the raw samples. The inferred Preview row, meanwhile, WAS anchored to the
/// refined outro end, so the two disagreed by exactly the refinement delta.
/// </remarks>
[Collection(PluginStateCollection.Name)]
public sealed class BlackFrameSegmentPersistenceTests : IDisposable
{
    private const long Second = TimeSpan.TicksPerSecond;

    private readonly SegmentDbFixture _fixture = new();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IMediaSourceManager _mediaSourceManager = Substitute.For<IMediaSourceManager>();
    private readonly IChapterManager _chapterManager = Substitute.For<IChapterManager>();
    private readonly IKeyframeManager _keyframeManager = Substitute.For<IKeyframeManager>();
    private readonly FfmpegBlackFrameService _blackFrameService;
    private readonly Guid _itemId = Guid.NewGuid();

    public BlackFrameSegmentPersistenceTests()
    {
        _blackFrameService = Substitute.ForPartsOf<FfmpegBlackFrameService>(
            Substitute.For<IMediaEncoder>(),
            Substitute.For<IConfigurationManager>(),
            NullLogger<FfmpegBlackFrameService>.Instance);

        _mediaSourceManager.GetMediaStreams(_itemId).Returns(new List<MediaStream>());
        _chapterManager.GetChapters(_itemId).Returns(new List<ChapterInfo>());
        _keyframeManager.GetKeyframeData(_itemId).Returns(new List<KeyframeData>().AsReadOnly());
    }

    public void Dispose() => _fixture.Dispose();

    private BlackFrameProvider CreateProvider()
    {
        var refiner = new SegmentRefiner(_blackFrameService, NullLogger<SegmentRefiner>.Instance);
        var pipeline = new RefinementPipeline(
            refiner,
            new ChapterSnapper(_chapterManager, NullLogger<ChapterSnapper>.Instance),
            new KeyframeSnapper(_keyframeManager, NullLogger<KeyframeSnapper>.Instance));

        return new BlackFrameProvider(
            _blackFrameService,
            _libraryManager,
            _mediaSourceManager,
            _fixture.Factory,
            pipeline,
            NullLogger<BlackFrameProvider>.Instance);
    }

    private void GivenMovie(long runtimeSeconds)
    {
        var item = new Movie
        {
            Id = _itemId,
            Name = "Test",
            Path = "/media/test.mkv",
            RunTimeTicks = runtimeSeconds * Second,
        };
        _libraryManager.GetItemById(_itemId).Returns(item);
    }

    /// <summary>Returns a black-frame run at [startSeconds, endSeconds] sampled every 100ms.</summary>
    private static List<(long TimestampTicks, double BlackPercentage)> Run(double startSeconds, double endSeconds)
    {
        var frames = new List<(long, double)>();
        for (var t = startSeconds; t <= endSeconds + 1e-9; t += 0.1)
        {
            frames.Add(((long)(t * Second), 100.0));
        }

        return frames;
    }

    private void GivenBlackFrames(
        List<(long TimestampTicks, double BlackPercentage)> intro,
        List<(long TimestampTicks, double BlackPercentage)> outro)
    {
        _blackFrameService.DetectBlackFramesAsync(
                Arg.Any<string>(), Arg.Any<double>(), Arg.Is<double>(s => s < 1.0), Arg.Any<double>(),
                Arg.Any<(int, int, int, int)?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(intro);

        _blackFrameService.DetectBlackFramesAsync(
                Arg.Any<string>(), Arg.Any<double>(), Arg.Is<double>(s => s >= 1.0), Arg.Any<double>(),
                Arg.Any<(int, int, int, int)?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(outro);

        _blackFrameService.DetectCropAsync(
                Arg.Any<string>(), Arg.Any<double>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((((int, int, int, int)?)null));
    }

    /// <summary>Silence at the given absolute second, used to pull a boundary during refinement.</summary>
    private void GivenSilenceAt(double atSeconds)
    {
        _blackFrameService.DetectSilenceAsync(
                Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(),
                Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<(double, double)> { (atSeconds - 0.5, atSeconds + 0.5) });
    }

    [Fact]
    public async Task RefinedBoundaries_ArePersisted()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableBlackFrameProvider = true;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
        });

        GivenMovie(runtimeSeconds: 600);
        GivenBlackFrames(Run(20, 22), []);

        // Silence 3s past the raw cluster end pulls the refined end to 23s.
        GivenSilenceAt(23);

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        var intro = db.ChapterAnalysisResults.Single(r => r.MatchedChapterName == "blackframe-intro");
        Assert.Equal((int)MediaSegmentType.Intro, intro.SegmentType);
        Assert.Equal(23 * Second, intro.EndTicks);
    }

    [Fact]
    public async Task ServedSegments_MatchPersistedRefinedBoundaries()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableBlackFrameProvider = true;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
        });

        GivenMovie(runtimeSeconds: 600);
        GivenBlackFrames(Run(20, 22), []);
        GivenSilenceAt(23);

        var provider = CreateProvider();
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);

        var served = await provider.GetMediaSegments(
            new MediaSegmentGenerationRequest { ItemId = _itemId, ExistingSegments = [] },
            CancellationToken.None);

        var intro = Assert.Single(served, s => s.Type == MediaSegmentType.Intro);
        Assert.Equal(23 * Second, intro.EndTicks);
    }

    /// <summary>
    /// The inferred Preview must start exactly where the served Outro ends. When the outro was
    /// re-derived unrefined at serve time but the preview stored the refined end, the two
    /// overlapped or left a gap.
    /// </summary>
    [Fact]
    public async Task PreviewStart_EqualsServedOutroEnd()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableBlackFrameProvider = true;
            c.EnablePreviewInference = true;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
            c.MinPreviewDurationSeconds = 2;
            c.MaxPreviewDurationSeconds = 60;
        });

        GivenMovie(runtimeSeconds: 600);

        // Outro cluster starts at 560s; the selector always ends the outro at runtime, so the raw
        // outro is [560, 600]. Silence at 596s is inside the end boundary's inward window, so
        // refinement pulls the end back to 596 and leaves a 4s tail for the preview.
        GivenBlackFrames([], Run(560, 562));
        GivenSilenceAt(596);

        var provider = CreateProvider();
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);

        var served = await provider.GetMediaSegments(
            new MediaSegmentGenerationRequest { ItemId = _itemId, ExistingSegments = [] },
            CancellationToken.None);

        var outro = Assert.Single(served, s => s.Type == MediaSegmentType.Outro);
        var preview = Assert.Single(served, s => s.Type == MediaSegmentType.Preview);
        Assert.Equal(outro.EndTicks, preview.StartTicks);
        Assert.Equal(600 * Second, preview.EndTicks);
    }

    /// <summary>
    /// A trailing gap below the preview minimum is noise, not a teaser, and is folded into the
    /// outro instead of surfacing as a misleading one-second Preview.
    /// </summary>
    [Fact]
    public async Task ShortTrailingGap_IsAbsorbedIntoOutro()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableBlackFrameProvider = true;
            c.EnablePreviewInference = true;
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
            c.MinPreviewDurationSeconds = 10;
        });

        GivenMovie(runtimeSeconds: 600);

        // Outro [560, 600] with the raw end at runtime already; refinement disabled.
        GivenBlackFrames([], Run(560, 562));

        var provider = CreateProvider();
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);

        var served = await provider.GetMediaSegments(
            new MediaSegmentGenerationRequest { ItemId = _itemId, ExistingSegments = [] },
            CancellationToken.None);

        Assert.DoesNotContain(served, s => s.Type == MediaSegmentType.Preview);
        var outro = Assert.Single(served, s => s.Type == MediaSegmentType.Outro);
        Assert.Equal(600 * Second, outro.EndTicks);
    }

    [Fact]
    public async Task ReAnalysisWithoutCleanup_IsIdempotent()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableBlackFrameProvider = true;
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
        });

        GivenMovie(runtimeSeconds: 600);
        GivenBlackFrames(Run(20, 22), []);

        var provider = CreateProvider();
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        Assert.Single(db.AnalysisStatuses);
        Assert.Single(db.ChapterAnalysisResults, r => r.MatchedChapterName == "blackframe-intro");
    }

    /// <summary>
    /// Clustering and duration settings are cheap to re-apply, so a change to them must be
    /// replayable from the cached samples without another ffmpeg scan.
    /// </summary>
    [Fact]
    public async Task RebuildFromCache_ReappliesSettingsWithoutRescanning()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableBlackFrameProvider = true;
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
            c.MinIntroDurationSeconds = 5;
        });

        GivenMovie(runtimeSeconds: 600);
        GivenBlackFrames(Run(20, 22), []);

        var provider = CreateProvider();
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);
        _blackFrameService.ClearReceivedCalls();

        // Raise the minimum intro duration above the detected cluster end so it no longer qualifies.
        scope.Configuration.MinIntroDurationSeconds = 60;
        await provider.RebuildSegmentsFromCacheAsync(_itemId, CancellationToken.None);

        await _blackFrameService.DidNotReceive().DetectBlackFramesAsync(
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(),
            Arg.Any<(int, int, int, int)?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        using var db = _fixture.CreateContext();
        Assert.DoesNotContain(db.ChapterAnalysisResults, r => r.MatchedChapterName == "blackframe-intro");
        Assert.NotEmpty(db.BlackFrameResults);
    }

    [Fact]
    public async Task StatusCarriesSegmentConfigHash()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableBlackFrameProvider = true;
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
        });

        GivenMovie(runtimeSeconds: 600);
        GivenBlackFrames(Run(20, 22), []);

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        var status = db.AnalysisStatuses.Single();
        Assert.Equal(ConfigHasher.BlackFrameSegments(scope.Configuration), status.ConfigHash);
    }

    [Fact]
    public async Task CleanupRemovesEveryOwnedSentinel()
    {
        using var scope = new PluginConfigScope(c =>
        {
            c.EnableBlackFrameProvider = true;
            c.EnablePreviewInference = true;
            c.EnableSilenceRefinement = false;
            c.EnableChapterSnapping = false;
            c.EnableKeyframeSnapping = false;
        });

        GivenMovie(runtimeSeconds: 600);
        GivenBlackFrames(Run(20, 22), Run(560, 562));

        var provider = CreateProvider();
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);
        await provider.CleanupExtractedData(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        Assert.Empty(db.ChapterAnalysisResults);
        Assert.Empty(db.BlackFrameResults);
        Assert.Empty(db.AnalysisStatuses);
    }
}
