using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;

/// <summary>
/// Scheduled task that imports segment data from the intro-skipper plugin database.
/// Imported segments are stored as <see cref="ChapterAnalysisResult"/> rows and
/// <see cref="AnalysisStatus"/> rows under all three provider names so they are
/// served by <c>ChapterNameProvider</c> and prevent re-analysis by the other providers.
/// </summary>
public class ImportIntroSkipperDataTask : IScheduledTask
{
    /// <summary>
    /// Sentinel <see cref="ChapterAnalysisResult.MatchedChapterName"/> value used for rows
    /// produced by this task. Treated as "foreign" by <see cref="Providers.ChapterNameProvider"/>
    /// so chapter-config changes don't wipe the imported data on the next analyze run.
    /// </summary>
    internal const string MatchedName = "intro-skipper import";

    /// <summary>
    /// Hard upper bound on the decompressed size of a single intro-skipper fingerprint blob.
    /// Real fingerprints are well under 1 MiB; anything larger is treated as a malformed
    /// or hostile cache row and skipped to avoid memory exhaustion via a Brotli bomb.
    /// </summary>
    internal const int MaxDecompressedFingerprintBytes = 16 * 1024 * 1024;

    private static readonly string[] _allProviderNames = [ProviderNames.ChapterName, ProviderNames.BlackFrame, ProviderNames.Chromaprint];

    private static readonly JsonSerializerOptions _fingerprintJsonOptions = new() { MaxDepth = 4 };

    private readonly IApplicationPaths _applicationPaths;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ImportIntroSkipperDataTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportIntroSkipperDataTask"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="dbContextFactory">Database context factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public ImportIntroSkipperDataTask(
        IApplicationPaths applicationPaths,
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILibraryManager libraryManager,
        ILogger<ImportIntroSkipperDataTask> logger)
    {
        _applicationPaths = applicationPaths;
        _dbContextFactory = dbContextFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Import Intro Skipper Data";

    /// <inheritdoc />
    public string Key => "SegmentRecognitionImportIntroSkipper";

    /// <inheritdoc />
    public string Description => "Imports existing segment data from the intro-skipper plugin database. " +
        "Imported segments are treated as native data by all providers.";

    /// <inheritdoc />
    public string Category => "Segment Recognition";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var introSkipperDbPath = Path.Join(_applicationPaths.DataPath, "introskipper", "introskipper.db");
        if (!File.Exists(introSkipperDbPath))
        {
            _logger.LogInformation("Intro-skipper database not found at {Path}, nothing to import", introSkipperDbPath);
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Starting intro-skipper data import from {Path}", introSkipperDbPath);

        // Read all segments from intro-skipper, grouped by ItemId
        var segmentsByItem = ReadIntroSkipperSegments(introSkipperDbPath, cancellationToken);
        if (segmentsByItem.Count == 0)
        {
            _logger.LogInformation("No valid segments found in intro-skipper database");
            progress.Report(100);
            return;
        }

        // Compute the config hash from introskipper's settings so that staleness detection
        // correctly flags imported data when the user's config differs from what introskipper used.
        var configHash = ComputeImportConfigHash(_applicationPaths.PluginConfigurationsPath);
        _logger.LogInformation("Using config hash {Hash} for imported segments", configHash);

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var imported = 0;
        var skipped = 0;
        var itemIds = segmentsByItem.Keys.ToList();

        // Pre-load all existing analysis statuses for the items to import (avoids N+1 queries in the loop).
        // Chunked to stay below SQLite's parameter limit (999), which Contains() expands to one bind per id.
        var existingStatuses = new HashSet<(Guid ItemId, string ProviderName)>();
        foreach (var chunk in itemIds.Chunk(500))
        {
            var rows = await db.AnalysisStatuses
                .Where(s => chunk.Contains(s.ItemId))
                .Select(s => new { s.ItemId, s.ProviderName })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                existingStatuses.Add((row.ItemId, row.ProviderName));
            }
        }

        for (int i = 0; i < itemIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var itemId = itemIds[i];

            // Skip if ChapterName provider already has an AnalysisStatus for this item
            // (either from a previous import or from actual chapter analysis)
            if (existingStatuses.Contains((itemId, ProviderNames.ChapterName)))
            {
                skipped++;
                continue;
            }

            // Write all segments for this item as ChapterAnalysisResult rows
            foreach (var (segmentType, startTicks, endTicks) in segmentsByItem[itemId])
            {
                db.ChapterAnalysisResults.Add(new ChapterAnalysisResult
                {
                    ItemId = itemId,
                    SegmentType = segmentType,
                    StartTicks = startTicks,
                    EndTicks = endTicks,
                    MatchedChapterName = MatchedName,
                    ConfigHash = configHash,
                    CreatedAt = DateTime.UtcNow
                });
            }

            // Mark all providers as analyzed for this item:
            // - ChapterName with HasResults=true so it serves the imported segments
            // - BlackFrame and Chromaprint with HasResults=false so they don't re-analyze
            foreach (var providerName in _allProviderNames.Where(pn => !existingStatuses.Contains((itemId, pn))))
            {
                db.AnalysisStatuses.Add(new AnalysisStatus
                {
                    ItemId = itemId,
                    ProviderName = providerName,
                    AnalyzedAt = DateTime.UtcNow,
                    HasResults = providerName == ProviderNames.ChapterName
                });
                existingStatuses.Add((itemId, providerName));
            }

            imported++;

            // Batch saves to avoid holding too many entities in memory
            if (i % 100 == 0)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                // Reserve 70..100 for the fingerprint import phase below.
                progress.Report((double)i / itemIds.Count * 70);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Second phase: import chromaprint fingerprints from introskipper-cache.db.
        // The segment-import phase ran in its own transaction first so that even if
        // fingerprint import fails or is cancelled, segment data is preserved.
        progress.Report(70);
        var fingerprintsImported = await ImportChromaprintFingerprintsAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);

        _logger.LogInformation(
            "Intro-skipper import complete: {Imported} items imported, {Skipped} items skipped (already analyzed), {Fingerprints} chromaprint fingerprints imported",
            imported,
            skipped,
            fingerprintsImported);
    }

    /// <summary>
    /// Imports chromaprint fingerprints from intro-skipper's <c>introskipper-cache.db</c>.
    /// Fingerprints are stored as Brotli-compressed UTF-8 JSON of <c>uint[]</c> in the source DB;
    /// we decompress, reinterpret as a little-endian byte stream, and write into our
    /// <see cref="ChromaprintResult"/> table so they participate in cross-episode comparison.
    /// </summary>
    /// <returns>The number of <see cref="ChromaprintResult"/> rows written.</returns>
    private async Task<int> ImportChromaprintFingerprintsAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var cacheDbPath = Path.Join(_applicationPaths.DataPath, "introskipper", "introskipper-cache.db");
        if (!File.Exists(cacheDbPath))
        {
            _logger.LogInformation("Intro-skipper cache DB not found at {Path}, skipping fingerprint import", cacheDbPath);
            return 0;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var introHash = ConfigHasher.ChromaprintIntro(config);
        var creditsHash = ConfigHasher.ChromaprintCredits(config);

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Pre-load existing (ItemId, Region) keys so we don't overwrite live fingerprints.
        var existingKeys = (await db.ChromaprintResults
            .Select(r => new { r.ItemId, r.Region })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Select(r => (r.ItemId, r.Region))
            .ToHashSet();

        var imported = 0;
        var skippedNotEpisode = 0;
        var skippedNoLibraryItem = 0;
        var skippedAlreadyHasFingerprint = 0;
        var malformed = 0;
        var batched = 0;
        var rowIndex = 0;
        long? totalRows = null;

        using var connection = new SqliteConnection($"Data Source={cacheDbPath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM DetectionCache WHERE Mode IN (0, 1) AND Type = 0";
            totalRows = (long)(await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        if (totalRows == 0)
        {
            _logger.LogInformation("No chromaprint fingerprints found in intro-skipper cache");
            return 0;
        }

        _logger.LogInformation("Importing {Count} chromaprint fingerprints from {Path}", totalRows, cacheDbPath);

        using var command = connection.CreateCommand();
        // Mode 0 = Introduction, Mode 1 = Credits. Type 0 = Chromaprint (other types are
        // silence/keyframe/blackframe detection caches that don't map to our schema).
        command.CommandText = "SELECT ItemId, Mode, Start, \"End\", Data FROM DetectionCache WHERE Mode IN (0, 1) AND Type = 0";

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowIndex++;

            if (!Guid.TryParse(reader.GetString(0), out var itemId))
            {
                continue;
            }

            var mode = reader.GetInt32(1);
            var startSeconds = reader.GetDouble(2);
            var endSeconds = reader.GetDouble(3);
            var compressed = (byte[])reader.GetValue(4);

            var region = mode == 0 ? SegmentSourceNames.RegionIntro : SegmentSourceNames.RegionCredits;

            if (existingKeys.Contains((itemId, region)))
            {
                skippedAlreadyHasFingerprint++;
                continue;
            }

            // Only episodes get fingerprinted in our pipeline (GetGroupId returns Empty otherwise).
            // Lookup the live SeasonId; intro-skipper's cache may include items that are no longer
            // in the library or that have been re-keyed.
            var libraryItem = _libraryManager.GetItemById(itemId);
            if (libraryItem is null)
            {
                skippedNoLibraryItem++;
                continue;
            }

            if (libraryItem is not Episode episode)
            {
                skippedNotEpisode++;
                continue;
            }

            byte[] fingerprintBytes;
            try
            {
                fingerprintBytes = DecodeFingerprintBlob(compressed);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                malformed++;
                _logger.LogWarning(ex, "Skipping malformed fingerprint blob for item {ItemId} mode {Mode}", itemId, mode);
                continue;
            }

            if (fingerprintBytes.Length == 0)
            {
                malformed++;
                continue;
            }

            db.ChromaprintResults.Add(new ChromaprintResult
            {
                ItemId = itemId,
                Region = region,
                SeasonId = episode.SeasonId,
                FingerprintData = fingerprintBytes,
                AnalysisDurationSeconds = Math.Max(0, (int)(endSeconds - startSeconds)),
                ConfigHash = region == SegmentSourceNames.RegionCredits ? creditsHash : introHash,
                CreatedAt = DateTime.UtcNow
            });
            existingKeys.Add((itemId, region));
            imported++;
            batched++;

            if (batched >= 200)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                batched = 0;

                if (totalRows is long total && total > 0)
                {
                    // Map fingerprint progress into the 70..100 portion of the overall task.
                    progress.Report(70 + (((double)rowIndex / total) * 30));
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Imported {Imported} chromaprint fingerprints (skipped: {AlreadyPresent} already present, {NotEpisode} non-episode, {Missing} not in library, {Malformed} malformed)",
            imported,
            skippedAlreadyHasFingerprint,
            skippedNotEpisode,
            skippedNoLibraryItem,
            malformed);

        return imported;
    }

    /// <summary>
    /// Decodes an intro-skipper detection-cache blob into our raw <c>uint32</c> byte format.
    /// The blob is Brotli-compressed UTF-8 JSON of a <c>uint[]</c>; our schema stores the
    /// hashes as a contiguous little-endian <c>uint32</c> stream so the comparer can use
    /// <see cref="MemoryMarshal.Cast{TFrom, TTo}(Span{TFrom})"/> for zero-copy reads.
    /// </summary>
    private static byte[] DecodeFingerprintBlob(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);

        // Read the decompressed payload through a fixed-size buffer with a hard length cap
        // so a maliciously crafted blob cannot expand into multi-GB of RAM.
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxDecompressedFingerprintBytes)
            {
                throw new InvalidDataException(
                    $"Decompressed fingerprint blob exceeds {MaxDecompressedFingerprintBytes} bytes");
            }

            output.Write(buffer, 0, read);
        }

        var hashes = JsonSerializer.Deserialize<uint[]>(output.GetBuffer().AsSpan(0, (int)output.Length), _fingerprintJsonOptions)
            ?? [];
        if (hashes.Length == 0)
        {
            return [];
        }

        var bytes = new byte[hashes.Length * sizeof(uint)];
        MemoryMarshal.AsBytes(hashes.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Reads segments directly from the intro-skipper SQLite database, grouped by ItemId.
    /// </summary>
    private static Dictionary<Guid, List<(int SegmentType, long StartTicks, long EndTicks)>> ReadIntroSkipperSegments(
        string dbPath,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, List<(int, long, long)>>();

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        // Filter out source rows where Start >= End - intro-skipper occasionally stores swapped
        // or zero-length ranges that would surface as negative-duration "skip" prompts.
        command.CommandText = "SELECT ItemId, Type, Start, \"End\" FROM DbSegment WHERE \"End\" > 0.0 AND Start < \"End\"";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Guid.TryParse(reader.GetString(0), out var itemId))
            {
                continue;
            }

            var introSkipperType = reader.GetInt32(1);
            var startSeconds = reader.GetDouble(2);
            var endSeconds = reader.GetDouble(3);

            var segmentType = MapAnalysisMode(introSkipperType);
            if (segmentType < 0)
            {
                continue;
            }

            var startTicks = (long)(startSeconds * TimeSpan.TicksPerSecond);
            var endTicks = (long)(endSeconds * TimeSpan.TicksPerSecond);

            if (!results.TryGetValue(itemId, out var list))
            {
                list = [];
                results[itemId] = list;
            }

            list.Add((segmentType, startTicks, endTicks));
        }

        return results;
    }

    /// <summary>
    /// Maps intro-skipper AnalysisMode to Jellyfin MediaSegmentType integer values.
    /// </summary>
    /// <remarks>
    /// intro-skipper: 0=Introduction, 1=Credits, 2=Preview, 3=Recap, 4=Commercial.
    /// MediaSegmentType: 0=Unknown, 1=Commercial, 2=Preview, 3=Recap, 4=Outro, 5=Intro.
    /// </remarks>
    private static int MapAnalysisMode(int introSkipperType)
    {
        return introSkipperType switch
        {
            0 => 5, // Introduction -> Intro
            1 => 4, // Credits -> Outro
            2 => 2, // Preview -> Preview
            3 => 3, // Recap -> Recap
            4 => 1, // Commercial -> Commercial
            _ => -1
        };
    }

    /// <summary>
    /// Computes a <see cref="ConfigHasher.ChapterName"/> hash from the intro-skipper plugin's
    /// configuration XML. Duration limits are mapped to their equivalents in
    /// <see cref="PluginConfiguration"/>; chapter name arrays use our defaults since
    /// intro-skipper uses regex patterns that don't map to our array format.
    /// If the config file is not found, our default configuration values are used.
    /// </summary>
    /// <param name="pluginConfigPath">The Jellyfin plugin configurations directory path.</param>
    /// <returns>A config hash string for staleness detection.</returns>
    private string ComputeImportConfigHash(string pluginConfigPath)
    {
        var config = new PluginConfiguration();

        // Try known introskipper config file names (newer fork then original).
        string[] candidates = ["IntroSkipper.xml", "ConfusedPolarBear.Plugin.IntroSkipper.xml"];
        XDocument? doc = null;

        foreach (var path in candidates.Select(c => Path.Join(pluginConfigPath, c)).Where(File.Exists))
        {
            try
            {
                doc = XDocument.Load(path);
                _logger.LogInformation("Found intro-skipper config at {Path}", path);
                break;
            }
            catch (XmlException ex)
            {
                _logger.LogWarning(ex, "Failed to parse intro-skipper config at {Path}, using defaults", path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to parse intro-skipper config at {Path}, using defaults", path);
            }
        }

        if (doc?.Root is XElement root)
        {
            config.MinIntroDurationSeconds = ReadIntElement(root, "MinimumIntroDuration", config.MinIntroDurationSeconds);
            config.MaxIntroDurationSeconds = ReadIntElement(root, "MaximumIntroDuration", config.MaxIntroDurationSeconds);
            config.MinOutroDurationSeconds = ReadIntElement(root, "MinimumCreditsDuration", config.MinOutroDurationSeconds);
            config.MaxOutroDurationSeconds = ReadIntElement(root, "MaximumCreditsDuration", config.MaxOutroDurationSeconds);
            config.MaxMovieOutroDurationSeconds = ReadIntElement(root, "MaximumMovieCreditsDuration", config.MaxMovieOutroDurationSeconds);
        }
        else
        {
            _logger.LogInformation("No intro-skipper config found, using default values for config hash");
        }

        return ConfigHasher.ChapterName(config);
    }

    /// <summary>
    /// Reads an integer element value from an XML element, returning the default if not found or unparseable.
    /// </summary>
    private static int ReadIntElement(XElement parent, string name, int defaultValue)
    {
        var element = parent.Element(name);
        if (element is not null && int.TryParse(element.Value, out var value))
        {
            return value;
        }

        return defaultValue;
    }
}
