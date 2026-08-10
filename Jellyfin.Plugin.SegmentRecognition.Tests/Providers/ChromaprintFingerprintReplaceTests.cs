using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using NSubstitute.Extensions;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests that regenerating a fingerprint cannot destroy the one already stored.
/// </summary>
/// <remarks>
/// The retry used to delete the row and then re-extract. A failed extraction - unreadable file,
/// ffmpeg missing, cancellation - left the item with no fingerprint at all, so it dropped out of
/// its season's comparison until a later run rebuilt it.
/// </remarks>
[Collection(PluginStateCollection.Name)]
public sealed class ChromaprintFingerprintReplaceTests : IDisposable
{
    private const long Second = TimeSpan.TicksPerSecond;

    private readonly SegmentDbFixture _fixture = new();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IMediaSourceManager _mediaSourceManager = Substitute.For<IMediaSourceManager>();
    private readonly IMediaEncoder _mediaEncoder = Substitute.For<IMediaEncoder>();
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly Guid _seasonId = Guid.NewGuid();

    public void Dispose() => _fixture.Dispose();

    private ChromaprintProvider CreateProvider(FfmpegChromaprintService? chromaprintService = null)
    {
        var chromaprint = chromaprintService
            ?? new FfmpegChromaprintService(_mediaEncoder, NullLogger<FfmpegChromaprintService>.Instance);
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

    private async Task GivenStoredFingerprintAsync(int coveredSeconds, string configHash)
    {
        _libraryManager.GetItemById(_itemId).Returns(new Episode
        {
            Id = _itemId,
            Name = "E1",
            Path = "/media/e1.mkv",
            IndexNumber = 1,
            SeasonId = _seasonId,
            RunTimeTicks = 3600 * Second,
        });
        _mediaSourceManager.GetMediaStreams(_itemId).Returns(new List<MediaStream>());

        using var db = _fixture.Factory.CreateDbContext();
        db.ChromaprintResults.Add(new ChromaprintResult
        {
            ItemId = _itemId,
            Region = SegmentSourceNames.RegionIntro,
            SeasonId = _seasonId,
            FingerprintData = [1, 2, 3, 4],
            AnalysisDurationSeconds = coveredSeconds,
            RegionStartTicks = 0,
            ConfigHash = configHash,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<ChromaprintResult?> StoredAsync()
    {
        using var db = _fixture.Factory.CreateDbContext();
        return await db.ChromaprintResults.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ItemId == _itemId, TestContext.Current.CancellationToken);
    }

    /// <summary>The guarantee: a failed re-extraction leaves the narrower fingerprint in place.</summary>
    [Fact]
    public async Task FailedWideningKeepsTheExistingFingerprint()
    {
        using var _ = new PluginConfigScope();
        await GivenStoredFingerprintAsync(600, ConfigHasher.ChromaprintIntro(new()));

        // No usable encoder path, so extraction throws rather than returning data.
        _mediaEncoder.EncoderPath.Returns(string.Empty);

        await Assert.ThrowsAnyAsync<Exception>(() => CreateProvider()
            .GenerateFingerprintAsync(_itemId, SegmentSourceNames.RegionIntro, CancellationToken.None, 1800));

        var row = await StoredAsync();
        Assert.NotNull(row);
        Assert.Equal(600, row!.AnalysisDurationSeconds);
        Assert.Equal<byte[]>([1, 2, 3, 4], row.FingerprintData);
    }

    /// <summary>
    /// A stored fingerprint already wide enough, under the current config, is left alone - so the
    /// retry cannot loop and the common path stays free of work.
    /// </summary>
    [Fact]
    public async Task AlreadyWideEnough_IsNotTouched()
    {
        using var _ = new PluginConfigScope();
        await GivenStoredFingerprintAsync(1800, ConfigHasher.ChromaprintIntro(new()));
        _mediaEncoder.EncoderPath.Returns(string.Empty);

        // Would throw if it tried to extract.
        await CreateProvider().GenerateFingerprintAsync(
            _itemId, SegmentSourceNames.RegionIntro, CancellationToken.None, 1800);

        Assert.Equal(1800, (await StoredAsync())!.AnalysisDurationSeconds);
    }

    /// <summary>
    /// The zero-width row written when extraction yields nothing is final. Comparing it on width
    /// would make it look permanently too narrow and re-run ffmpeg against an item with no usable
    /// audio on every single pass.
    /// </summary>
    [Fact]
    public async Task ZeroWidthSentinelIsNotRetriedOnWidth()
    {
        using var _ = new PluginConfigScope();
        await GivenStoredFingerprintAsync(0, ConfigHasher.ChromaprintIntro(new()));
        _mediaEncoder.EncoderPath.Returns(string.Empty);

        // Would throw if it tried to extract again.
        await CreateProvider().GenerateFingerprintAsync(
            _itemId, SegmentSourceNames.RegionIntro, CancellationToken.None, 1800);

        Assert.Equal(0, (await StoredAsync())!.AnalysisDurationSeconds);
    }

    /// <summary>
    /// A stub for the ffmpeg run, so the outcomes that are not exceptions can be reached.
    /// </summary>
    /// <param name="result">What the extraction returns.</param>
    /// <param name="before">Runs before it returns, to stand in for what happens meanwhile.</param>
    private FfmpegChromaprintService StubExtraction(byte[] result, Action? before = null)
    {
        var stub = Substitute.ForPartsOf<FfmpegChromaprintService>(
            _mediaEncoder, NullLogger<FfmpegChromaprintService>.Instance);

        stub.Configure()
            .GenerateFingerprintAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                before?.Invoke();
                return Task.FromResult(result);
            });

        return stub;
    }

    /// <summary>
    /// ffmpeg exiting cleanly with no output is not the same as ffmpeg failing, and it is not
    /// evidence worth destroying a working fingerprint over - a seek past the end of the audio or
    /// a momentarily unreadable file produces exactly this. It used to overwrite the fingerprint
    /// with the zero-width sentinel, which is final: never stale on width, never a retry
    /// candidate. One bad extraction retired the item permanently.
    /// </summary>
    [Fact]
    public async Task EmptyExtractionKeepsTheExistingFingerprint()
    {
        using var _ = new PluginConfigScope();
        await GivenStoredFingerprintAsync(600, ConfigHasher.ChromaprintIntro(new()));

        await CreateProvider(StubExtraction([])).GenerateFingerprintAsync(
            _itemId, SegmentSourceNames.RegionIntro, CancellationToken.None, 1800);

        var row = await StoredAsync();
        Assert.Equal(600, row!.AnalysisDurationSeconds);
        Assert.Equal<byte[]>([1, 2, 3, 4], row.FingerprintData);
    }

    /// <summary>
    /// With nothing stored there is nothing to lose, so the sentinel is still written - otherwise
    /// an item with genuinely no usable audio would be re-extracted on every single pass.
    /// </summary>
    [Fact]
    public async Task EmptyExtractionStillRecordsTheSentinelWhenNothingIsStored()
    {
        using var _ = new PluginConfigScope();
        _libraryManager.GetItemById(_itemId).Returns(new Episode
        {
            Id = _itemId,
            Name = "E1",
            Path = "/media/e1.mkv",
            IndexNumber = 1,
            SeasonId = _seasonId,
            RunTimeTicks = 3600 * Second,
        });

        await CreateProvider(StubExtraction([])).GenerateFingerprintAsync(
            _itemId, SegmentSourceNames.RegionIntro, CancellationToken.None, 0);

        var row = await StoredAsync();
        Assert.NotNull(row);
        Assert.Equal(0, row!.AnalysisDurationSeconds);
        Assert.Empty(row.FingerprintData);
    }

    /// <summary>
    /// Whether a row existed is read before an extraction that can take minutes. The scheduled
    /// task does not take the per-item lock the Recalculate endpoint uses, so a row can appear in
    /// between - which turned the store into a primary-key violation on (ItemId, Region).
    /// </summary>
    [Fact]
    public async Task RowAppearingDuringExtractionIsReplacedRatherThanColliding()
    {
        using var _ = new PluginConfigScope();
        _libraryManager.GetItemById(_itemId).Returns(new Episode
        {
            Id = _itemId,
            Name = "E1",
            Path = "/media/e1.mkv",
            IndexNumber = 1,
            SeasonId = _seasonId,
            RunTimeTicks = 3600 * Second,
        });
        _mediaSourceManager.GetMediaStreams(_itemId).Returns(new List<MediaStream>());

        // Nothing stored when the provider looks; a competing writer lands one mid-extraction.
        var stub = StubExtraction(
            [9, 9, 9, 9],
            before: () =>
            {
                using var db = _fixture.Factory.CreateDbContext();
                db.ChromaprintResults.Add(new ChromaprintResult
                {
                    ItemId = _itemId,
                    Region = SegmentSourceNames.RegionIntro,
                    SeasonId = _seasonId,
                    FingerprintData = [1, 2, 3, 4],
                    AnalysisDurationSeconds = 600,
                    RegionStartTicks = 0,
                    ConfigHash = ConfigHasher.ChromaprintIntro(new()),
                    CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            });

        await CreateProvider(stub).GenerateFingerprintAsync(
            _itemId, SegmentSourceNames.RegionIntro, CancellationToken.None, 1800);

        using var verify = _fixture.Factory.CreateDbContext();
        var rows = await verify.ChromaprintResults.AsNoTracking()
            .Where(r => r.ItemId == _itemId && r.Region == SegmentSourceNames.RegionIntro)
            .ToListAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal<byte[]>([9, 9, 9, 9], row.FingerprintData);
    }

    /// <summary>
    /// A fingerprint produced under a different configuration is rebuilt even when it is wide
    /// enough - the caller no longer deletes it first, so the provider has to notice.
    /// </summary>
    [Fact]
    public async Task StaleConfigHashIsRebuiltEvenWhenWideEnough()
    {
        using var _ = new PluginConfigScope();
        await GivenStoredFingerprintAsync(1800, "some-older-hash");
        _mediaEncoder.EncoderPath.Returns(string.Empty);

        // Attempting extraction proves it did not take the early return.
        await Assert.ThrowsAnyAsync<Exception>(() => CreateProvider()
            .GenerateFingerprintAsync(_itemId, SegmentSourceNames.RegionIntro, CancellationToken.None, 0));

        Assert.Equal("some-older-hash", (await StoredAsync())!.ConfigHash);
    }
}
