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
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests the cleanup Jellyfin calls when an item's extracted data is pruned.
/// </summary>
/// <remarks>
/// This is the hook that fires when Jellyfin notices a source file has been replaced
/// (<c>MetadataService.BeforeSaveInternal</c> -> <c>DeleteExternalItemDataAsync</c> ->
/// <c>MediaSegmentManager.DeleteSegmentsAsync</c>), so it is the whole mechanism by which a
/// fingerprint taken from a file that no longer exists gets discarded. It is also what the
/// <c>Recalculate</c> endpoint runs for <c>ClearCache</c>, which makes it the recovery path for
/// items whose prune was missed.
/// </remarks>
[Collection(PluginStateCollection.Name)]
public sealed class ChromaprintCleanupTests : IDisposable
{
    private const long Second = TimeSpan.TicksPerSecond;

    private readonly SegmentDbFixture _fixture = new();
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly Guid _otherItemId = Guid.NewGuid();
    private readonly Guid _seasonId = Guid.NewGuid();

    public void Dispose() => _fixture.Dispose();

    private ChromaprintProvider CreateProvider()
    {
        var chromaprint = new FfmpegChromaprintService(
            Substitute.For<IMediaEncoder>(), Substitute.For<IConfigurationManager>(),
            NullLogger<FfmpegChromaprintService>.Instance);
        var blackFrame = Substitute.ForPartsOf<FfmpegBlackFrameService>(
            Substitute.For<IMediaEncoder>(), Substitute.For<IConfigurationManager>(),
            NullLogger<FfmpegBlackFrameService>.Instance);
        var pipeline = new RefinementPipeline(
            new SegmentRefiner(blackFrame, NullLogger<SegmentRefiner>.Instance),
            new ChapterSnapper(Substitute.For<IChapterManager>(), NullLogger<ChapterSnapper>.Instance),
            new KeyframeSnapper(Substitute.For<IKeyframeManager>(), NullLogger<KeyframeSnapper>.Instance));

        return new ChromaprintProvider(
            chromaprint, Substitute.For<ILibraryManager>(), Substitute.For<IMediaSourceManager>(),
            _fixture.Factory, pipeline, NullLogger<ChromaprintProvider>.Instance);
    }

    private async Task SeedAsync()
    {
        using var db = _fixture.Factory.CreateDbContext();

        foreach (var (itemId, region) in new[]
        {
            (_itemId, SegmentSourceNames.RegionIntro),
            (_itemId, SegmentSourceNames.RegionCredits),
            (_otherItemId, SegmentSourceNames.RegionIntro),
        })
        {
            db.ChromaprintResults.Add(new ChromaprintResult
            {
                ItemId = itemId,
                Region = region,
                SeasonId = _seasonId,
                FingerprintData = [1, 2, 3, 4],
                AnalysisDurationSeconds = 600,
                RegionStartTicks = 0,
                ConfigHash = ConfigHasher.ChromaprintIntro(new()),
                CreatedAt = DateTime.UtcNow,
            });
        }

        // Every sentinel that can share the table, on the same item.
        foreach (var name in new[]
        {
            SegmentSourceNames.ChromaprintIntro,
            SegmentSourceNames.ChromaprintCredits,
            SegmentSourceNames.ChromaprintPreview,
            SegmentSourceNames.BlackFrameIntro,
            SegmentSourceNames.BlackFrameOutro,
            SegmentSourceNames.EdlImportName,
            "Intro",
        })
        {
            db.ChapterAnalysisResults.Add(new ChapterAnalysisResult
            {
                ItemId = _itemId,
                SegmentType = (int)MediaSegmentType.Intro,
                StartTicks = 10 * Second,
                EndTicks = 40 * Second,
                MatchedChapterName = name,
                ConfigHash = ConfigHasher.ChromaprintComparison(new()),
                CreatedAt = DateTime.UtcNow,
            });
        }

        // A sibling item's chromaprint segment, to prove the delete is scoped to one item.
        db.ChapterAnalysisResults.Add(new ChapterAnalysisResult
        {
            ItemId = _otherItemId,
            SegmentType = (int)MediaSegmentType.Intro,
            StartTicks = 10 * Second,
            EndTicks = 40 * Second,
            MatchedChapterName = SegmentSourceNames.ChromaprintIntro,
            ConfigHash = ConfigHasher.ChromaprintComparison(new()),
            CreatedAt = DateTime.UtcNow,
        });

        foreach (var provider in new[] { ProviderNames.Chromaprint, ProviderNames.BlackFrame })
        {
            db.AnalysisStatuses.Add(new AnalysisStatus
            {
                ItemId = _itemId,
                ProviderName = provider,
                AnalyzedAt = DateTime.UtcNow,
                HasResults = true,
                ConfigHash = ConfigHasher.ChromaprintComparison(new()),
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The guarantee: nothing derived from the discarded fingerprint survives. A surviving segment
    /// row carries the current comparison hash, which makes AnalyzeRegionAsync skip the item on
    /// every future run - so the segment computed from the previous copy of the file would be
    /// republished to Jellyfin, which had just pruned it.
    /// </summary>
    [Fact]
    public async Task RemovesEverythingDerivedFromTheFingerprint()
    {
        using var _ = new PluginConfigScope();
        await SeedAsync();

        await CreateProvider().CleanupExtractedData(_itemId, CancellationToken.None);

        using var db = _fixture.Factory.CreateDbContext();
        Assert.Empty(await db.ChromaprintResults.AsNoTracking()
            .Where(r => r.ItemId == _itemId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.ChapterAnalysisResults.AsNoTracking()
            .Where(r => r.ItemId == _itemId
                && SegmentSourceNames.ChromaprintOwned.Contains(r.MatchedChapterName))
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.AnalysisStatuses.AsNoTracking()
            .Where(s => s.ItemId == _itemId && s.ProviderName == ProviderNames.Chromaprint)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Sibling providers keep their own segments in the same table and are asked to clean up
    /// separately; deleting theirs here would strip segments from an item whose black-frame or
    /// chapter-name status still claims to have results.
    /// </summary>
    [Fact]
    public async Task LeavesOtherProvidersSegmentsAlone()
    {
        using var _ = new PluginConfigScope();
        await SeedAsync();

        await CreateProvider().CleanupExtractedData(_itemId, CancellationToken.None);

        using var db = _fixture.Factory.CreateDbContext();
        var survivors = (await db.ChapterAnalysisResults.AsNoTracking()
            .Where(r => r.ItemId == _itemId)
            .Select(r => r.MatchedChapterName)
            .ToListAsync(TestContext.Current.CancellationToken))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["Intro", SegmentSourceNames.BlackFrameIntro, SegmentSourceNames.BlackFrameOutro, SegmentSourceNames.EdlImportName],
            survivors);

        Assert.Single(await db.AnalysisStatuses.AsNoTracking()
            .Where(s => s.ItemId == _itemId && s.ProviderName == ProviderNames.BlackFrame)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Only the pruned item is touched, not the rest of its season.</summary>
    [Fact]
    public async Task LeavesOtherItemsAlone()
    {
        using var _ = new PluginConfigScope();
        await SeedAsync();

        await CreateProvider().CleanupExtractedData(_itemId, CancellationToken.None);

        using var db = _fixture.Factory.CreateDbContext();
        Assert.Single(await db.ChromaprintResults.AsNoTracking()
            .Where(r => r.ItemId == _otherItemId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.ChapterAnalysisResults.AsNoTracking()
            .Where(r => r.ItemId == _otherItemId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }
}
