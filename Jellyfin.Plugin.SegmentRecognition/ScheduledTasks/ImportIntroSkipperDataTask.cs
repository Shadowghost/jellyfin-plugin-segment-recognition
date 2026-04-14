using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Configuration;
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
    private static readonly string[] _allProviderNames = [ProviderNames.ChapterName, ProviderNames.BlackFrame, ProviderNames.Chromaprint];

    private readonly IApplicationPaths _applicationPaths;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILogger<ImportIntroSkipperDataTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportIntroSkipperDataTask"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="dbContextFactory">Database context factory.</param>
    /// <param name="logger">Logger.</param>
    public ImportIntroSkipperDataTask(
        IApplicationPaths applicationPaths,
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILogger<ImportIntroSkipperDataTask> logger)
    {
        _applicationPaths = applicationPaths;
        _dbContextFactory = dbContextFactory;
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

        // Pre-load all existing analysis statuses for the items to import (avoids N+1 queries in the loop)
        var existingStatuses = (await db.AnalysisStatuses
            .Where(s => itemIds.Contains(s.ItemId))
            .Select(s => new { s.ItemId, s.ProviderName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Select(s => (s.ItemId, s.ProviderName))
            .ToHashSet();

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
                    MatchedChapterName = "intro-skipper import",
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
                progress.Report((double)i / itemIds.Count * 100);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);

        _logger.LogInformation(
            "Intro-skipper import complete: {Imported} items imported, {Skipped} items skipped (already analyzed)",
            imported,
            skipped);
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
        command.CommandText = "SELECT ItemId, Type, Start, \"End\" FROM DbSegment WHERE \"End\" > 0.0";

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
            catch (Exception ex)
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
