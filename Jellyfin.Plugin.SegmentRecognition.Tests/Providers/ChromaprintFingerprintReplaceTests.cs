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

    private ChromaprintProvider CreateProvider()
    {
        var chromaprint = new FfmpegChromaprintService(_mediaEncoder, NullLogger<FfmpegChromaprintService>.Instance);
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
