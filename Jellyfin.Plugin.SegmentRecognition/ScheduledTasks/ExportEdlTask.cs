using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;

/// <summary>
/// Exports Jellyfin media segments as EDL (Edit Decision List) sidecar files.
/// Writes a <c>.edl</c> file next to each media file using the Kodi/MPlayer-compatible format.
/// </summary>
public class ExportEdlTask : IScheduledTask
{
    private const int PageSize = 100;

    private static readonly BaseItemKind[] _itemTypes = [BaseItemKind.Episode, BaseItemKind.Movie];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly ILogger<ExportEdlTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportEdlTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSegmentManager">The media segment manager.</param>
    /// <param name="logger">The logger.</param>
    public ExportEdlTask(
        ILibraryManager libraryManager,
        IMediaSegmentManager mediaSegmentManager,
        ILogger<ExportEdlTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Export EDL Files";

    /// <inheritdoc />
    public string Key => "SegmentRecognitionExportEdl";

    /// <inheritdoc />
    public string Description => "Exports media segments as EDL (Edit Decision List) sidecar files " +
        "next to each media file. Compatible with Kodi, MPlayer, and other players that support the EDL format.";

    /// <inheritdoc />
    public string Category => "Segment Recognition";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        _logger.LogInformation("EDL export task starting");
        progress.Report(0);

        // Snapshot the id set once and page over it in memory. Paging the library query itself
        // with StartIndex requires a total order to be well-defined, and no unique sort key is
        // exposed - an unstable order silently skips and duplicates items across pages.
        var allIds = GetAllItemIds();
        var totalItems = allIds.Count;
        if (totalItems == 0)
        {
            _logger.LogInformation("No items to export, task complete");
            progress.Report(100);
            return;
        }

        var written = 0;
        var skipped = 0;
        var deleted = 0;
        var errors = 0;
        var processed = 0;

        foreach (var idChunk in allIds.Chunk(PageSize))
        {
            var page = GetItemsByIds(idChunk);

            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var (didWrite, didDelete) = await ExportItemEdlAsync(item, cancellationToken).ConfigureAwait(false);
                    if (didWrite)
                    {
                        written++;
                    }
                    else if (didDelete)
                    {
                        deleted++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to export EDL for \"{ItemName}\" ({Path})", item.Name, item.Path);
                    errors++;
                }

                processed++;
                progress.Report(100.0 * processed / totalItems);
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "EDL export complete: {Written} written, {Skipped} unchanged, {Deleted} removed (no segments), {Errors} errors",
            written,
            skipped,
            deleted,
            errors);
    }

    /// <summary>
    /// Exports an EDL file for a single item.
    /// </summary>
    /// <returns>A tuple: (written, deleted). Both false means skipped/unchanged.</returns>
    private async Task<(bool Written, bool Deleted)> ExportItemEdlAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return (false, false);
        }

        var edlPath = Path.ChangeExtension(item.Path, ".edl");
        var libraryOptions = _libraryManager.GetLibraryOptions(item);
        var segments = (await _mediaSegmentManager.GetSegmentsAsync(item, null, libraryOptions).ConfigureAwait(false))
            .OrderBy(s => s.StartTicks)
            .ToList();

        if (segments.Count == 0)
        {
            // Clean up a stale EDL file, but only one we wrote ourselves. A hand-authored sidecar
            // (or one from another tool) is user data and an input to EdlImportProvider - deleting
            // it because we currently have nothing to say would destroy it irrecoverably.
            if (File.Exists(edlPath))
            {
                if (!EdlImportProvider.IsGeneratedByThisPlugin(edlPath))
                {
                    _logger.LogDebug("Leaving externally-authored EDL file {Path} untouched", edlPath);
                    return (false, false);
                }

                File.Delete(edlPath);
                _logger.LogDebug("Deleted stale EDL file {Path}", edlPath);
                return (false, true);
            }

            return (false, false);
        }

        return await WriteEdlAsync(edlPath, segments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Written, bool Deleted)> WriteEdlAsync(
        string edlPath,
        List<MediaSegmentDto> segments,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>(segments.Count);
        foreach (var segment in segments)
        {
            var action = GetEdlAction(segment.Type);
            if (action < 0)
            {
                continue;
            }

            var startSeconds = segment.StartTicks / (double)TimeSpan.TicksPerSecond;
            var endSeconds = segment.EndTicks / (double)TimeSpan.TicksPerSecond;

            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0:F3}\t{1:F3}\t{2}\t{3}",
                startSeconds,
                endSeconds,
                action,
                GetEdlTypeName(segment.Type)));
        }

        if (lines.Count == 0)
        {
            return (false, false);
        }

        var body = string.Join("\n", lines) + "\n";

        // The marker identifies the file as ours: EdlImportProvider skips it (so exports don't get
        // re-imported as a second provider's segments) and the delete path above refuses to touch
        // anything without it.
        var content = EdlImportProvider.GeneratedMarker + "\n" + body;

        if (File.Exists(edlPath))
        {
            var existing = await File.ReadAllTextAsync(edlPath, cancellationToken).ConfigureAwait(false);

            if (!EdlImportProvider.IsGeneratedByThisPlugin(edlPath))
            {
                // Exports written before the marker existed are unmarked but are byte-for-byte
                // what this task would produce. Recognising that lets an upgrade re-stamp its own
                // old output instead of orphaning it forever, without ever guessing about a file
                // it cannot prove it wrote.
                if (!string.Equals(existing, body, StringComparison.Ordinal))
                {
                    _logger.LogDebug("Leaving externally-authored EDL file {Path} untouched", edlPath);
                    return (false, false);
                }

                _logger.LogDebug("Re-stamping previously exported EDL file {Path} with the generated marker", edlPath);
            }
            else if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return (false, false);
            }
        }

        await File.WriteAllTextAsync(edlPath, content, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Wrote EDL file {Path} ({Count} entries)", edlPath, lines.Count);
        return (true, false);
    }

    /// <summary>
    /// Maps a Jellyfin <see cref="MediaSegmentType"/> to a Kodi/MPlayer EDL action code.
    /// Action codes: 0 = cut/skip, 1 = mute, 2 = scene marker, 3 = commercial break.
    /// </summary>
    private static int GetEdlAction(MediaSegmentType type)
    {
        return type switch
        {
            MediaSegmentType.Intro => 0,     // Skip intro
            MediaSegmentType.Outro => 0,     // Skip outro/credits
            MediaSegmentType.Commercial => 3, // Commercial break
            MediaSegmentType.Recap => 0,     // Skip recap
            MediaSegmentType.Preview => 0,   // Skip preview
            _ => -1 // Unknown - don't export
        };
    }

    /// <summary>
    /// Returns the segment type name written as the optional 4th column in EDL files.
    /// This extended format allows lossless round-trips between export and import
    /// while remaining compatible with standard EDL parsers (which ignore extra columns).
    /// </summary>
    private static string GetEdlTypeName(MediaSegmentType type)
    {
        return type switch
        {
            MediaSegmentType.Intro => "Intro",
            MediaSegmentType.Outro => "Outro",
            MediaSegmentType.Commercial => "Commercial",
            MediaSegmentType.Recap => "Recap",
            MediaSegmentType.Preview => "Preview",
            _ => "Unknown"
        };
    }

    private IReadOnlyList<Guid> GetAllItemIds()
    {
        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            IncludeItemTypes = _itemTypes,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true,
            IncludeOwnedItems = true
        };

        return _libraryManager.GetItemIds(query);
    }

    private IReadOnlyList<BaseItem> GetItemsByIds(Guid[] ids)
    {
        var query = new InternalItemsQuery
        {
            ItemIds = ids,
            DtoOptions = new DtoOptions(true),
            IncludeOwnedItems = true
        };

        return _libraryManager.GetItemList(query);
    }
}
