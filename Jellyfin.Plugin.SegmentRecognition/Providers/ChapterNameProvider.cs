using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Providers;

/// <summary>
/// Detects segments by matching chapter names against configured patterns with word-boundary matching.
/// </summary>
public class ChapterNameProvider : IMediaSegmentProvider, IHasOrder
{
    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);

    private readonly IChapterManager _chapterManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILogger<ChapterNameProvider> _logger;

    /// <summary>
    /// Pre-compiled regexes per segment type, rebuilt when the plugin configuration changes.
    /// </summary>
    private volatile Dictionary<MediaSegmentType, Regex> _regexes;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterNameProvider"/> class.
    /// </summary>
    /// <param name="chapterManager">The chapter manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="logger">The logger.</param>
    public ChapterNameProvider(
        IChapterManager chapterManager,
        ILibraryManager libraryManager,
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILogger<ChapterNameProvider> logger)
    {
        _chapterManager = chapterManager;
        _libraryManager = libraryManager;
        _dbContextFactory = dbContextFactory;
        _logger = logger;

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _regexes = BuildRegexes(config);

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }
    }

    /// <inheritdoc />
    public string Name => ProviderNames.ChapterName;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return ValueTask.FromResult(config.EnableChapterNameProvider && item is Video);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existingStatus = await db.AnalysisStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ItemId == request.ItemId && s.ProviderName == Name, cancellationToken)
            .ConfigureAwait(false);

        if (existingStatus is null || !existingStatus.HasResults)
        {
            return [];
        }

        var cached = await db.ChapterAnalysisResults
            .AsNoTracking()
            .Where(r => r.ItemId == request.ItemId
                && r.MatchedChapterName != SegmentSourceNames.ChromaprintIntro
                && r.MatchedChapterName != SegmentSourceNames.ChromaprintCredits
                && r.MatchedChapterName != SegmentSourceNames.ChromaprintPreview
                && r.MatchedChapterName != EdlImportProvider.MatchedName)
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

    /// <summary>
    /// Analyzes an item's chapters and stores results in the database.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AnalyzeAsync(Guid itemId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var configHash = ConfigHasher.ChapterName(config);
        var item = _libraryManager.GetItemById(itemId);
        var isMovie = item is Movie;
        var chapters = _chapterManager.GetChapters(itemId);

        // Key is (SegmentType, MatchedChapterName) — keep only the first match per segment type
        var dbResults = new Dictionary<(int SegmentType, string ChapterName), ChapterAnalysisResult>();

        for (int i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            if (string.IsNullOrWhiteSpace(chapter.Name))
            {
                continue;
            }

            var segmentType = MatchChapterName(chapter.Name);
            if (segmentType is null)
            {
                continue;
            }

            var segmentTypeInt = (int)segmentType.Value;
            var key = (segmentTypeInt, chapter.Name);
            if (dbResults.ContainsKey(key))
            {
                _logger.LogDebug(
                    "Chapter \"{ChapterName}\" matched as {Type} but a match already exists for this type, skipping",
                    chapter.Name,
                    segmentType.Value);
                continue;
            }

            // Also skip if we already have a result for this segment type (different chapter name)
            if (dbResults.Keys.Any(k => k.SegmentType == segmentTypeInt))
            {
                _logger.LogDebug(
                    "Chapter \"{ChapterName}\" matched as {Type} but a match already exists for this type, skipping",
                    chapter.Name,
                    segmentType.Value);
                continue;
            }

            var startTicks = chapter.StartPositionTicks;
            var endTicks = i + 1 < chapters.Count ? chapters[i + 1].StartPositionTicks : startTicks;

            var durationSeconds = (endTicks - startTicks) / (double)TimeSpan.TicksPerSecond;
            if (!IsValidDuration(segmentType.Value, durationSeconds, config, isMovie))
            {
                _logger.LogDebug(
                    "Chapter \"{ChapterName}\" matched as {Type} but duration {Duration:F1}s is outside valid range, skipping",
                    chapter.Name,
                    segmentType.Value,
                    durationSeconds);
                continue;
            }

            dbResults[key] = new ChapterAnalysisResult
            {
                ItemId = itemId,
                SegmentType = segmentTypeInt,
                StartTicks = startTicks,
                EndTicks = endTicks,
                MatchedChapterName = chapter.Name,
                ConfigHash = configHash,
                CreatedAt = DateTime.UtcNow
            };
        }

        db.ChapterAnalysisResults.AddRange(dbResults.Values);
        db.AnalysisStatuses.Add(new AnalysisStatus
        {
            ItemId = itemId,
            ProviderName = Name,
            AnalyzedAt = DateTime.UtcNow,
            HasResults = dbResults.Values.Count > 0
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("ChapterName: found {Count} segments for item {ItemId} from {ChapterCount} chapters", dbResults.Values.Count, itemId, chapters.Count);
    }

    /// <summary>
    /// Removes all cached analysis data for the specified item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        db.ChapterAnalysisResults.RemoveRange(
            db.ChapterAnalysisResults.Where(r => r.ItemId == itemId));
        db.AnalysisStatuses.RemoveRange(
            db.AnalysisStatuses.Where(s => s.ItemId == itemId && s.ProviderName == Name));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether a segment duration falls within the configured valid range for its type.
    /// </summary>
    /// <param name="type">The segment type.</param>
    /// <param name="durationSeconds">The segment duration in seconds.</param>
    /// <param name="config">The plugin configuration containing duration limits.</param>
    /// <param name="isMovie">Whether the item is a movie (uses movie-specific outro limits).</param>
    /// <returns><c>true</c> if the duration is within the valid range; otherwise <c>false</c>.</returns>
    internal static bool IsValidDuration(MediaSegmentType type, double durationSeconds, PluginConfiguration config, bool isMovie)
    {
        if (durationSeconds <= 0)
        {
            return false;
        }

        var maxOutro = isMovie ? config.MaxMovieOutroDurationSeconds : config.MaxOutroDurationSeconds;

        return type switch
        {
            MediaSegmentType.Intro => durationSeconds >= config.MinIntroDurationSeconds
                                      && durationSeconds <= config.MaxIntroDurationSeconds,
            MediaSegmentType.Outro => durationSeconds >= config.MinOutroDurationSeconds
                                      && durationSeconds <= maxOutro,
            MediaSegmentType.Recap => durationSeconds >= config.MinIntroDurationSeconds
                                      && durationSeconds <= maxOutro,
            MediaSegmentType.Preview => durationSeconds >= config.MinIntroDurationSeconds
                                        && durationSeconds <= config.MaxIntroDurationSeconds,
            _ => true
        };
    }

    private MediaSegmentType? MatchChapterName(string chapterName)
    {
        foreach (var (type, regex) in _regexes)
        {
            try
            {
                if (regex.IsMatch(chapterName))
                {
                    return type;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Malformed or adversarial chapter name — skip this type
            }
        }

        return null;
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        if (e is PluginConfiguration config)
        {
            _regexes = BuildRegexes(config);
        }
    }

    /// <summary>
    /// Builds compiled regexes for all segment types. Each regex combines all patterns
    /// for a segment type into a single alternation:
    /// <c>(^|\s)(Pattern1|Pattern2|...)(?!\s+End)(\s|:|$)</c>.
    /// </summary>
    /// <param name="config">The plugin configuration containing chapter name patterns.</param>
    /// <returns>A dictionary mapping segment types to their compiled regexes.</returns>
    internal static Dictionary<MediaSegmentType, Regex> BuildRegexes(PluginConfiguration config)
    {
        var regexes = new Dictionary<MediaSegmentType, Regex>();
        AddRegex(regexes, MediaSegmentType.Intro, config.IntroChapterNames);
        AddRegex(regexes, MediaSegmentType.Outro, config.OutroChapterNames);
        AddRegex(regexes, MediaSegmentType.Recap, config.RecapChapterNames);
        AddRegex(regexes, MediaSegmentType.Preview, config.PreviewChapterNames);
        return regexes;
    }

    private static void AddRegex(Dictionary<MediaSegmentType, Regex> regexes, MediaSegmentType type, string[] patterns)
    {
        if (patterns.Length == 0)
        {
            return;
        }

        var alternation = string.Join("|", patterns.Select(Regex.Escape));
        var pattern = @"(^|\s)(" + alternation + @")(?!\s+End)(\s|:|$)";
        regexes[type] = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, _regexTimeout);
    }
}
