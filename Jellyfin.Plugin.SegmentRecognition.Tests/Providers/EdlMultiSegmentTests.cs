using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.Providers;

/// <summary>
/// Regression tests for EDL files that contain more than one segment of the same type, and for
/// the export/import loop guard.
/// </summary>
public sealed class EdlMultiSegmentTests : IDisposable
{
    private readonly SegmentDbFixture _fixture = new();
    private readonly string _tempDir;

    public EdlMultiSegmentTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "segrec-edl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private EdlImportProvider CreateProvider() => new(
        Substitute.For<ILibraryManager>(),
        _fixture.Factory,
        NullLogger<EdlImportProvider>.Instance);

    private string WriteEdl(string content)
    {
        var path = Path.Join(_tempDir, Guid.NewGuid().ToString("N") + ".edl");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Three commercial breaks is an entirely ordinary EDL. Every row carries the same
    /// "edl-import" sentinel, so under the old (ItemId, SegmentType, MatchedChapterName) key only
    /// the first could ever be stored - the rest collided and threw on the serving path.
    /// </summary>
    [Fact]
    public void MultipleCommercialBreaks_AllParseAndPersist()
    {
        var itemId = Guid.NewGuid();
        var path = WriteEdl(
            "60.000\t120.000\t3\n"
            + "600.000\t660.000\t3\n"
            + "1200.000\t1260.000\t3\n");

        var segments = CreateProvider().ParseEdlFile(path, itemId, 1800 * TimeSpan.TicksPerSecond);

        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.Equal(MediaSegmentType.Commercial, s.Type));

        using var db = _fixture.CreateContext();
        db.ChapterAnalysisResults.AddRange(segments.Select(s => new Jellyfin.Plugin.SegmentRecognition.Data.Entities.ChapterAnalysisResult
        {
            ItemId = itemId,
            SegmentType = (int)s.Type,
            StartTicks = s.StartTicks,
            EndTicks = s.EndTicks,
            MatchedChapterName = "edl-import",
            CreatedAt = DateTime.UtcNow,
        }));

        db.SaveChanges();
        Assert.Equal(3, db.ChapterAnalysisResults.Count());
    }

    /// <summary>
    /// The position-based classifier emits [Outro][Preview][Preview…] for a three-cut file, which
    /// is another same-type-twice case.
    /// </summary>
    [Fact]
    public void MultiplePreviewsFromClassifier_AllPersist()
    {
        var itemId = Guid.NewGuid();
        var path = WriteEdl(
            "1000.000\t1100.000\t0\n"
            + "1200.000\t1250.000\t0\n"
            + "1300.000\t1350.000\t0\n");

        var segments = CreateProvider().ParseEdlFile(path, itemId, 1800 * TimeSpan.TicksPerSecond);

        Assert.Equal(2, segments.Count(s => s.Type == MediaSegmentType.Preview));

        using var db = _fixture.CreateContext();
        db.ChapterAnalysisResults.AddRange(segments.Select(s => new Jellyfin.Plugin.SegmentRecognition.Data.Entities.ChapterAnalysisResult
        {
            ItemId = itemId,
            SegmentType = (int)s.Type,
            StartTicks = s.StartTicks,
            EndTicks = s.EndTicks,
            MatchedChapterName = "edl-import",
            CreatedAt = DateTime.UtcNow,
        }));

        db.SaveChanges();
        Assert.Equal(3, db.ChapterAnalysisResults.Count());
    }

    [Fact]
    public void DuplicateLines_AreCollapsed()
    {
        var itemId = Guid.NewGuid();
        var path = WriteEdl("60.000\t120.000\t3\n60.000\t120.000\t3\n");

        var segments = CreateProvider().ParseEdlFile(path, itemId, 1800 * TimeSpan.TicksPerSecond);

        Assert.Single(segments);
    }

    [Fact]
    public void GeneratedFile_IsRecognized()
    {
        var path = WriteEdl(EdlImportProvider.GeneratedMarker + "\n0.000\t30.000\t0\tIntro\n");

        Assert.True(EdlImportProvider.IsGeneratedByThisPlugin(path));
    }

    [Fact]
    public void HandAuthoredFile_IsNotRecognized()
    {
        var path = WriteEdl("0.000\t30.000\t0\n");

        Assert.False(EdlImportProvider.IsGeneratedByThisPlugin(path));
    }

    [Fact]
    public void FileWithOtherLeadingComment_IsNotRecognized()
    {
        var path = WriteEdl("# my notes\n0.000\t30.000\t0\n");

        Assert.False(EdlImportProvider.IsGeneratedByThisPlugin(path));
    }

    [Fact]
    public void MarkerAfterBlankLines_IsStillRecognized()
    {
        var path = WriteEdl("\n\n" + EdlImportProvider.GeneratedMarker + "\n0.000\t30.000\t0\tIntro\n");

        Assert.True(EdlImportProvider.IsGeneratedByThisPlugin(path));
    }

    [Fact]
    public void MissingFile_IsNotRecognized()
    {
        Assert.False(EdlImportProvider.IsGeneratedByThisPlugin(
            Path.Join(_tempDir, "does-not-exist.edl")));
    }

    [Fact]
    public void SegmentCap_IsStillEnforced()
    {
        var itemId = Guid.NewGuid();
        var lines = string.Concat(Enumerable.Range(0, EdlImportProvider.MaxEdlSegments + 50)
            .Select(i => string.Create(
                CultureInfo.InvariantCulture,
                $"{i * 10}.000\t{(i * 10) + 5}.000\t3\n")));

        var segments = CreateProvider().ParseEdlFile(WriteEdl(lines), itemId, 0);

        Assert.Equal(EdlImportProvider.MaxEdlSegments, segments.Count);
    }
}
