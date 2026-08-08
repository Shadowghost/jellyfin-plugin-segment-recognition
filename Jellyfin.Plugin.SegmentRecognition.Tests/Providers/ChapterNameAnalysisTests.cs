using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.Services;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Tests for <see cref="ChapterNameProvider.AnalyzeAsync"/>, which previously had no coverage at
/// all - only the pure matching helpers were tested.
/// </summary>
[Collection(PluginStateCollection.Name)]
public sealed class ChapterNameAnalysisTests : IDisposable
{
    private const long Minute = TimeSpan.TicksPerMinute;

    private readonly SegmentDbFixture _fixture = new();
    private readonly IChapterManager _chapterManager = Substitute.For<IChapterManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly Guid _itemId = Guid.NewGuid();

    public void Dispose() => _fixture.Dispose();

    private ChapterNameProvider CreateProvider() => new(
        _chapterManager,
        _libraryManager,
        _fixture.Factory,
        NullLogger<ChapterNameProvider>.Instance);

    private void GivenItem(long runtimeTicks)
    {
        var item = new Movie { Id = _itemId, Name = "Test", RunTimeTicks = runtimeTicks };
        _libraryManager.GetItemById(_itemId).Returns(item);
    }

    private void GivenChapters(params (string Name, long StartTicks)[] chapters)
    {
        _chapterManager.GetChapters(_itemId).Returns(
            chapters.Select(c => new ChapterInfo { Name = c.Name, StartPositionTicks = c.StartTicks }).ToList());
    }

    /// <summary>
    /// The regression this file exists for: a trailing "Credits" chapter is the most common outro
    /// layout there is, and it was silently dropped because the last chapter was given an end
    /// position equal to its start, producing a zero duration that the validity check rejects.
    /// </summary>
    [Fact]
    public async Task FinalChapter_IsBoundedByRuntime()
    {
        using var scope = new PluginConfigScope();
        GivenItem(runtimeTicks: 24 * Minute);
        GivenChapters(("Opening", 0), ("Episode", 2 * Minute), ("Credits", 22 * Minute));

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        var outro = db.ChapterAnalysisResults.Single(r => r.SegmentType == (int)MediaSegmentType.Outro);
        Assert.Equal(22 * Minute, outro.StartTicks);
        Assert.Equal(24 * Minute, outro.EndTicks);
    }

    [Fact]
    public async Task FinalChapter_IsSkippedWhenRuntimeUnknown()
    {
        using var scope = new PluginConfigScope();
        GivenItem(runtimeTicks: 0);
        GivenChapters(("Credits", 22 * Minute));

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        Assert.Empty(db.ChapterAnalysisResults);
        Assert.False(db.AnalysisStatuses.Single().HasResults);
    }

    [Fact]
    public async Task FinalChapter_IsSkippedWhenRuntimePrecedesIt()
    {
        using var scope = new PluginConfigScope();

        // Corrupt metadata: the chapter starts after the reported runtime ends.
        GivenItem(runtimeTicks: 10 * Minute);
        GivenChapters(("Credits", 22 * Minute));

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        Assert.Empty(db.ChapterAnalysisResults);
    }

    [Fact]
    public async Task IntermediateChapters_StillBoundedByNextChapter()
    {
        using var scope = new PluginConfigScope();
        GivenItem(runtimeTicks: 24 * Minute);
        GivenChapters(("Intro", Minute), ("Main", 2 * Minute), ("Credits", 22 * Minute));

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        var intro = db.ChapterAnalysisResults.Single(r => r.SegmentType == (int)MediaSegmentType.Intro);
        Assert.Equal(Minute, intro.StartTicks);
        Assert.Equal(2 * Minute, intro.EndTicks);
    }

    /// <summary>
    /// A recalculation with <c>clearCache=false</c> calls straight into AnalyzeAsync without a
    /// cleanup. The unconditional inserts that used to live here threw a unique-constraint
    /// violation, which the job layer swallowed and reported as a per-item failure.
    /// </summary>
    [Fact]
    public async Task ReAnalysisWithoutCleanup_IsIdempotent()
    {
        using var scope = new PluginConfigScope();
        GivenItem(runtimeTicks: 24 * Minute);
        GivenChapters(("Intro", Minute), ("Main", 2 * Minute), ("Credits", 22 * Minute));

        var provider = CreateProvider();
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);
        await provider.AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        Assert.Equal(2, db.ChapterAnalysisResults.Count());
        Assert.Single(db.AnalysisStatuses);
    }

    /// <summary>
    /// An item that matched nothing has no result rows to carry a config hash, so the hash has to
    /// live on the status row - otherwise widening a keyword list can never re-analyze exactly the
    /// items the change was meant to catch.
    /// </summary>
    [Fact]
    public async Task ZeroMatchItem_RecordsConfigHashOnStatus()
    {
        using var scope = new PluginConfigScope();
        GivenItem(runtimeTicks: 24 * Minute);
        GivenChapters(("Scene 1", 0), ("Scene 2", 10 * Minute));

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        var status = db.AnalysisStatuses.Single();
        Assert.False(status.HasResults);
        Assert.Equal(ConfigHasher.ChapterName(scope.Configuration), status.ConfigHash);
    }

    [Fact]
    public async Task MatchedItem_RecordsConfigHashOnStatus()
    {
        using var scope = new PluginConfigScope();
        GivenItem(runtimeTicks: 24 * Minute);
        GivenChapters(("Intro", 0), ("Main", Minute));

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        var status = db.AnalysisStatuses.Single();
        Assert.True(status.HasResults);
        Assert.Equal(ConfigHasher.ChapterName(scope.Configuration), status.ConfigHash);
    }

    /// <summary>
    /// Cleanup must not touch rows owned by sibling providers, which share this table behind
    /// sentinel names.
    /// </summary>
    [Fact]
    public async Task ReAnalysis_PreservesForeignProviderRows()
    {
        using var scope = new PluginConfigScope();
        GivenItem(runtimeTicks: 24 * Minute);
        GivenChapters(("Intro", 0), ("Main", Minute));

        using (var db = _fixture.CreateContext())
        {
            db.ChapterAnalysisResults.Add(new Jellyfin.Plugin.SegmentRecognition.Data.Entities.ChapterAnalysisResult
            {
                ItemId = _itemId,
                SegmentType = (int)MediaSegmentType.Outro,
                StartTicks = 20 * Minute,
                EndTicks = 24 * Minute,
                MatchedChapterName = "chromaprint-credits",
                ConfigHash = "other",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var verify = _fixture.CreateContext();
        Assert.Single(verify.ChapterAnalysisResults, r => r.MatchedChapterName == "chromaprint-credits");
    }

    /// <summary>
    /// Duplicate chapter titles of the same type must not collide on the widened unique index.
    /// </summary>
    [Fact]
    public async Task RepeatedChapterNameOfSameType_DoesNotThrow()
    {
        using var scope = new PluginConfigScope();
        GivenItem(runtimeTicks: 60 * Minute);
        GivenChapters(
            ("Ad break", 10 * Minute),
            ("Content", 11 * Minute),
            ("Ad break", 30 * Minute),
            ("More content", 31 * Minute));

        await CreateProvider().AnalyzeAsync(_itemId, CancellationToken.None);

        using var db = _fixture.CreateContext();
        var commercials = db.ChapterAnalysisResults
            .Where(r => r.SegmentType == (int)MediaSegmentType.Commercial)
            .ToList();
        Assert.Equal(2, commercials.Count);
        Assert.Equal(
            new List<long> { 10 * Minute, 30 * Minute },
            commercials.Select(c => c.StartTicks).OrderBy(t => t).ToList());
    }
}
