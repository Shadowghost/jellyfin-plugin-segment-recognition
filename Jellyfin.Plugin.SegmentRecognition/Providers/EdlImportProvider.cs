using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Providers;

/// <summary>
/// Imports media segments from EDL (Edit Decision List) sidecar files.
/// Unlike other providers, this reads <c>.edl</c> files directly when queried
/// and caches the results to avoid re-parsing on subsequent scans.
/// Supports an extended EDL format with an optional 4th column for the segment type name,
/// falling back to a position-based heuristic for standard 3-column EDL files.
/// </summary>
public class EdlImportProvider : IMediaSegmentProvider, IHasOrder
{
    /// <summary>
    /// The <see cref="ChapterAnalysisResult.MatchedChapterName"/> value used for EDL-imported segments.
    /// </summary>
    internal const string MatchedName = "edl-import";

    private static readonly Dictionary<string, MediaSegmentType> _typeNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Intro"] = MediaSegmentType.Intro,
        ["Outro"] = MediaSegmentType.Outro,
        ["Recap"] = MediaSegmentType.Recap,
        ["Preview"] = MediaSegmentType.Preview,
        ["Commercial"] = MediaSegmentType.Commercial,
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILogger<EdlImportProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EdlImportProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="logger">The logger.</param>
    public EdlImportProvider(
        ILibraryManager libraryManager,
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILogger<EdlImportProvider> logger)
    {
        _libraryManager = libraryManager;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "EdlImport";

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return ValueTask.FromResult(config.EnableEdlImportProvider && item is Video);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return [];
        }

        var edlPath = Path.ChangeExtension(item.Path, ".edl");
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(edlPath))
        {
            // EDL file removed — clean up any stale cached data.
            await CleanupCachedDataAsync(db, request.ItemId, cancellationToken).ConfigureAwait(false);
            return [];
        }

        // Check if we already imported this file and it hasn't changed since.
        var status = await db.AnalysisStatuses
            .FirstOrDefaultAsync(s => s.ItemId == request.ItemId && s.ProviderName == Name, cancellationToken)
            .ConfigureAwait(false);

        var lastWriteUtc = File.GetLastWriteTimeUtc(edlPath);

        if (status is not null && status.AnalyzedAt >= lastWriteUtc)
        {
            // EDL file hasn't changed — serve from cache.
            return await GetCachedSegmentsAsync(db, request.ItemId, cancellationToken).ConfigureAwait(false);
        }

        // Parse the EDL file.
        var runtimeTicks = item.RunTimeTicks ?? 0;
        var segments = ParseEdlFile(edlPath, request.ItemId, runtimeTicks);

        // Replace any previous cached results for this item.
        await db.ChapterAnalysisResults
            .Where(r => r.ItemId == request.ItemId && r.MatchedChapterName == MatchedName)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (segments.Count == 0)
        {
            _logger.LogDebug("No valid segments found in EDL file {Path}", edlPath);
            UpdateStatus(db, status, request.ItemId, hasResults: false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return [];
        }

        _logger.LogInformation("Imported {Count} segments from EDL file {Path}", segments.Count, edlPath);

        foreach (var seg in segments)
        {
            db.ChapterAnalysisResults.Add(new ChapterAnalysisResult
            {
                ItemId = request.ItemId,
                SegmentType = (int)seg.Type,
                StartTicks = seg.StartTicks,
                EndTicks = seg.EndTicks,
                MatchedChapterName = MatchedName,
                CreatedAt = DateTime.UtcNow
            });
        }

        UpdateStatus(db, status, request.ItemId, hasResults: true);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return segments;
    }

    /// <summary>
    /// Removes all cached EDL import data for the specified item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        db.ChapterAnalysisResults.RemoveRange(
            db.ChapterAnalysisResults.Where(r => r.ItemId == itemId && r.MatchedChapterName == MatchedName));
        db.AnalysisStatuses.RemoveRange(
            db.AnalysisStatuses.Where(s => s.ItemId == itemId && s.ProviderName == Name));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a Kodi/MPlayer-compatible EDL file into media segment DTOs.
    /// Supports an optional 4th column with the segment type name for lossless round-trips.
    /// Falls back to a position-based heuristic for standard 3-column EDL files.
    /// </summary>
    /// <param name="edlPath">Path to the EDL file.</param>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="runtimeTicks">The media runtime in ticks (0 if unknown).</param>
    /// <returns>A list of parsed media segment DTOs.</returns>
    internal List<MediaSegmentDto> ParseEdlFile(string edlPath, Guid itemId, long runtimeTicks)
    {
        var typed = new List<MediaSegmentDto>();
        var untyped = new List<(double StartSeconds, double EndSeconds)>();

        foreach (var trimmed in File.ReadLines(edlPath).Select(line => line.Trim()))
        {
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            // Split on tabs or spaces — some EDL generators use spaces.
            var parts = trimmed.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out var startSeconds)
                || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var endSeconds)
                || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var action))
            {
                _logger.LogDebug("Skipping unparseable EDL line in {Path}: {Line}", edlPath, trimmed);
                continue;
            }

            if (endSeconds <= startSeconds)
            {
                continue;
            }

            // Action 3 = commercial break (always mapped directly).
            if (action == 3)
            {
                typed.Add(new MediaSegmentDto
                {
                    ItemId = itemId,
                    Type = MediaSegmentType.Commercial,
                    StartTicks = SecondsToTicks(startSeconds),
                    EndTicks = SecondsToTicks(endSeconds)
                });
                continue;
            }

            // Only action 0 (cut/skip) is imported; action 1 (mute) and 2 (scene marker) are ignored.
            if (action != 0)
            {
                continue;
            }

            // Check for optional 4th column with explicit type name (our extended format).
            if (parts.Length >= 4 && _typeNameMap.TryGetValue(parts[3], out var explicitType))
            {
                typed.Add(new MediaSegmentDto
                {
                    ItemId = itemId,
                    Type = explicitType,
                    StartTicks = SecondsToTicks(startSeconds),
                    EndTicks = SecondsToTicks(endSeconds)
                });
                continue;
            }

            // Standard 3-column EDL — collect for position-based classification.
            untyped.Add((startSeconds, endSeconds));
        }

        // Classify untyped action-0 segments by position relative to runtime midpoint.
        if (untyped.Count > 0)
        {
            typed.AddRange(ClassifySkipSegments(untyped, itemId, runtimeTicks));
        }

        return typed;
    }

    /// <summary>
    /// Classifies action-0 (skip) segments by their position in the media file.
    /// </summary>
    /// <remarks>
    /// <para>Segments are split into first-half (start &lt; runtime/2) and second-half groups.</para>
    /// <para>First-half: if 2+ segments, earliest = Recap, next = Intro; if 1, Intro.</para>
    /// <para>Second-half: if 2+ segments, earliest = Outro, rest = Preview; if 1, Outro.</para>
    /// <para>This handles the common TV pattern: [Recap] [Intro] ... [Outro] [Preview].</para>
    /// </remarks>
    private static List<MediaSegmentDto> ClassifySkipSegments(
        List<(double StartSeconds, double EndSeconds)> segments,
        Guid itemId,
        long runtimeTicks)
    {
        var runtimeSeconds = runtimeTicks / (double)TimeSpan.TicksPerSecond;
        var midpoint = runtimeSeconds / 2.0;

        // If runtime is unknown, treat everything as first-half (all become Intro).
        var firstHalf = runtimeTicks > 0
            ? segments.Where(s => s.StartSeconds < midpoint).OrderBy(s => s.StartSeconds).ToList()
            : segments.OrderBy(s => s.StartSeconds).ToList();
        var secondHalf = runtimeTicks > 0
            ? segments.Where(s => s.StartSeconds >= midpoint).OrderBy(s => s.StartSeconds).ToList()
            : [];

        var result = new List<MediaSegmentDto>();

        // First half: [Recap] [Intro] [extra Intros...]
        for (int i = 0; i < firstHalf.Count; i++)
        {
            var type = (i == 0 && firstHalf.Count >= 2) ? MediaSegmentType.Recap : MediaSegmentType.Intro;
            result.Add(new MediaSegmentDto
            {
                ItemId = itemId,
                Type = type,
                StartTicks = SecondsToTicks(firstHalf[i].StartSeconds),
                EndTicks = SecondsToTicks(firstHalf[i].EndSeconds)
            });
        }

        // Second half: [Outro] [Preview] [extra Previews...]
        for (int i = 0; i < secondHalf.Count; i++)
        {
            var type = i == 0 ? MediaSegmentType.Outro : MediaSegmentType.Preview;
            result.Add(new MediaSegmentDto
            {
                ItemId = itemId,
                Type = type,
                StartTicks = SecondsToTicks(secondHalf[i].StartSeconds),
                EndTicks = SecondsToTicks(secondHalf[i].EndSeconds)
            });
        }

        return result;
    }

    private static long SecondsToTicks(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    private static async Task<IReadOnlyList<MediaSegmentDto>> GetCachedSegmentsAsync(
        SegmentDbContext db,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var cached = await db.ChapterAnalysisResults
            .AsNoTracking()
            .Where(r => r.ItemId == itemId && r.MatchedChapterName == MatchedName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return cached.Select(r => new MediaSegmentDto
        {
            ItemId = r.ItemId,
            Type = (MediaSegmentType)r.SegmentType,
            StartTicks = r.StartTicks,
            EndTicks = r.EndTicks
        }).ToList();
    }

    private void UpdateStatus(SegmentDbContext db, AnalysisStatus? existing, Guid itemId, bool hasResults)
    {
        if (existing is not null)
        {
            existing.AnalyzedAt = DateTime.UtcNow;
            existing.HasResults = hasResults;
        }
        else
        {
            db.AnalysisStatuses.Add(new AnalysisStatus
            {
                ItemId = itemId,
                ProviderName = Name,
                AnalyzedAt = DateTime.UtcNow,
                HasResults = hasResults
            });
        }
    }

    private async Task CleanupCachedDataAsync(SegmentDbContext db, Guid itemId, CancellationToken cancellationToken)
    {
        var status = await db.AnalysisStatuses
            .FirstOrDefaultAsync(s => s.ItemId == itemId && s.ProviderName == Name, cancellationToken)
            .ConfigureAwait(false);

        if (status is null)
        {
            return;
        }

        await db.ChapterAnalysisResults
            .Where(r => r.ItemId == itemId && r.MatchedChapterName == MatchedName)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        db.AnalysisStatuses.Remove(status);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
