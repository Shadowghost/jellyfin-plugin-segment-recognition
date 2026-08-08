using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.ScheduledTasks;

/// <summary>
/// Tests that EDL export never destroys a sidecar it did not write.
/// </summary>
/// <remarks>
/// The task used to delete any <c>.edl</c> next to an item with no segments. That file is also an
/// <em>input</em> - EdlImportProvider reads it - so a hand-authored sidecar could be deleted
/// simply because analysis had nothing to say that run.
/// </remarks>
public sealed class EdlExportSafetyTests : IDisposable
{
    private const long Second = TimeSpan.TicksPerSecond;

    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IMediaSegmentManager _segmentManager = Substitute.For<IMediaSegmentManager>();
    private readonly string _tempDir;
    private readonly string _mediaPath;
    private readonly string _edlPath;
    private readonly Movie _item;

    public EdlExportSafetyTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "segrec-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _mediaPath = Path.Join(_tempDir, "movie.mkv");
        _edlPath = Path.Join(_tempDir, "movie.edl");
        File.WriteAllText(_mediaPath, string.Empty);

        _item = new Movie { Id = Guid.NewGuid(), Name = "Movie", Path = _mediaPath, RunTimeTicks = 600 * Second };
        _libraryManager.GetLibraryOptions(Arg.Any<BaseItem>()).Returns(new LibraryOptions());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private ExportEdlTask CreateTask() => new(
        _libraryManager,
        _segmentManager,
        NullLogger<ExportEdlTask>.Instance);

    private void GivenSegments(params MediaSegmentDto[] segments)
    {
        _segmentManager
            .GetSegmentsAsync(Arg.Any<BaseItem>(), Arg.Any<IEnumerable<MediaSegmentType>?>(), Arg.Any<LibraryOptions>())
            .Returns(Task.FromResult<IEnumerable<MediaSegmentDto>>(segments));
    }

    private async Task<(bool Written, bool Deleted)> RunExportAsync()
    {
        var method = typeof(ExportEdlTask).GetMethod(
            "ExportItemEdlAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        return await (Task<(bool, bool)>)method.Invoke(CreateTask(), [_item, CancellationToken.None])!;
    }

    [Fact]
    public async Task HandAuthoredSidecar_IsNotDeletedWhenThereAreNoSegments()
    {
        const string authored = "12.500\t45.000\t0\n";
        await File.WriteAllTextAsync(_edlPath, authored, TestContext.Current.CancellationToken);
        GivenSegments();

        var (written, deleted) = await RunExportAsync();

        Assert.False(written);
        Assert.False(deleted);
        Assert.True(File.Exists(_edlPath));
        Assert.Equal(authored, await File.ReadAllTextAsync(_edlPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandAuthoredSidecar_IsNotOverwrittenWhenSegmentsExist()
    {
        const string authored = "12.500\t45.000\t0\n";
        await File.WriteAllTextAsync(_edlPath, authored, TestContext.Current.CancellationToken);
        GivenSegments(new MediaSegmentDto
        {
            ItemId = _item.Id,
            Type = MediaSegmentType.Intro,
            StartTicks = 0,
            EndTicks = 30 * Second,
        });

        var (written, _) = await RunExportAsync();

        Assert.False(written);
        Assert.Equal(authored, await File.ReadAllTextAsync(_edlPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GeneratedSidecar_IsDeletedWhenThereAreNoSegments()
    {
        await File.WriteAllTextAsync(_edlPath, EdlImportProvider.GeneratedMarker + "\n0.000\t30.000\t0\tIntro\n", TestContext.Current.CancellationToken);
        GivenSegments();

        var (_, deleted) = await RunExportAsync();

        Assert.True(deleted);
        Assert.False(File.Exists(_edlPath));
    }

    [Fact]
    public async Task ExportedFileCarriesTheGeneratedMarker()
    {
        GivenSegments(new MediaSegmentDto
        {
            ItemId = _item.Id,
            Type = MediaSegmentType.Intro,
            StartTicks = 0,
            EndTicks = 30 * Second,
        });

        var (written, _) = await RunExportAsync();

        Assert.True(written);
        Assert.True(EdlImportProvider.IsGeneratedByThisPlugin(_edlPath));
        Assert.StartsWith(EdlImportProvider.GeneratedMarker, await File.ReadAllTextAsync(_edlPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// The marker closes the export/import loop: without it, EdlImportProvider reads the plugin's
    /// own export back in and every segment is served twice, by two providers.
    /// </summary>
    [Fact]
    public async Task ExportedFileIsIgnoredByTheImporter()
    {
        GivenSegments(new MediaSegmentDto
        {
            ItemId = _item.Id,
            Type = MediaSegmentType.Intro,
            StartTicks = 0,
            EndTicks = 30 * Second,
        });

        await RunExportAsync();

        Assert.True(EdlImportProvider.IsGeneratedByThisPlugin(_edlPath));
    }

    /// <summary>
    /// Files written by an older build are unmarked but byte-identical to this task's output, so
    /// they get re-stamped rather than orphaned forever.
    /// </summary>
    [Fact]
    public async Task PreMarkerExport_IsRecognizedAndReStamped()
    {
        await File.WriteAllTextAsync(_edlPath, "0.000\t30.000\t0\tIntro\n", TestContext.Current.CancellationToken);
        GivenSegments(new MediaSegmentDto
        {
            ItemId = _item.Id,
            Type = MediaSegmentType.Intro,
            StartTicks = 0,
            EndTicks = 30 * Second,
        });

        var (written, _) = await RunExportAsync();

        Assert.True(written);
        Assert.True(EdlImportProvider.IsGeneratedByThisPlugin(_edlPath));
    }

    [Fact]
    public async Task UnchangedGeneratedFile_IsNotRewritten()
    {
        GivenSegments(new MediaSegmentDto
        {
            ItemId = _item.Id,
            Type = MediaSegmentType.Intro,
            StartTicks = 0,
            EndTicks = 30 * Second,
        });

        Assert.True((await RunExportAsync()).Written);
        Assert.False((await RunExportAsync()).Written);
    }
}
