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
using Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;
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
    /// <summary>
    /// MatchedChapterName sentinel values owned by other providers. These must be preserved
    /// when ChapterNameProvider cleans up its own rows. Array form is used so EF Core
    /// can translate the Contains call into a SQL <c>IN</c> clause.
    /// </summary>
    internal static readonly string[] ForeignSentinels =
    [
        SegmentSourceNames.ChromaprintIntro,
        SegmentSourceNames.ChromaprintCredits,
        SegmentSourceNames.ChromaprintPreview,
        SegmentSourceNames.BlackFramePreview,
        EdlImportProvider.MatchedName,
        ImportIntroSkipperDataTask.MatchedName,
    ];

    /// <summary>
    /// Characters that may precede a chapter keyword and still count as a word boundary:
    /// start-of-string, whitespace, or common separators/openers (<c>- / ( [</c>). This lets a
    /// base keyword match punctuation-delimited titles such as "(Intro)" or "Recap/Intro"
    /// without having to enumerate every bracketed variant as its own literal.
    /// </summary>
    private const string LeadingBoundary = @"(?:^|[\s\-/(\[])";

    /// <summary>
    /// Characters that may follow a chapter keyword and still count as a word boundary:
    /// whitespace, common separators/closers, sentence punctuation, or end-of-string. The
    /// boundary requirement still prevents substring matches such as "Intro" in "Introvert".
    /// </summary>
    private const string TrailingBoundary = @"(?:[\s:)\]/.,!?]|$)";

    /// <summary>
    /// Rejects keywords immediately followed by an "End" marker (e.g. "Intro End",
    /// "Credits: End"), which denote the end boundary of a segment rather than the segment
    /// itself. The trailing <c>\b</c> keeps words merely starting with "End" (e.g. "Ending",
    /// "Endgame") from being mistaken for the marker.
    /// </summary>
    private const string EndMarkerLookahead = @"(?![\s:]+End\b)";

    /// <summary>
    /// Name of the regex capture group holding the matched keyword, used to rank competing
    /// matches by specificity (longest matched keyword wins).
    /// </summary>
    private const string KeywordGroup = "keyword";

    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Hard upper bound on chapter-name length before regex matching. Real chapter titles
    /// are well under this; rejecting longer strings up front prevents pathological inputs
    /// from allocating large state inside the regex engine even when the per-call timeout
    /// would eventually fire.
    /// </summary>
    internal const int MaxChapterNameLength = 512;

    private readonly IChapterManager _chapterManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILogger<ChapterNameProvider> _logger;

    /// <summary>
    /// Pre-compiled regexes per segment type. Each segment type holds an array of
    /// regexes - one per configured chapter-name pattern - and a chapter is matched
    /// against a type if any of that type's regexes hits. Rebuilt when the plugin
    /// configuration changes.
    /// </summary>
    private volatile Dictionary<MediaSegmentType, Regex[]> _regexes;

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
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableChapterNameProvider)
        {
            return [];
        }

        try
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
        catch (ObjectDisposedException)
        {
            // Host is shutting down - the DbContextFactory's underlying service provider has been
            // disposed. Return empty rather than letting MediaSegmentManager log this as a failure.
            return [];
        }
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

        // Key is (SegmentType, MatchedChapterName). We allow the same SegmentType to appear
        // more than once per item - e.g. Intro → Commercial → Intro is a valid chapter
        // arrangement, and ad breaks recur per episode. The (type, name) compound key
        // prevents the rare case of literally duplicate chapter titles for the same type
        // from colliding on the DB primary key.
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
                    "Chapter \"{ChapterName}\" matched as {Type} but an identical (type, name) entry already exists, skipping",
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

        _logger.LogDebug("ChapterName: found {Count} segments for item {ItemId} from {ChapterCount} chapters", dbResults.Values.Count, itemId, chapters.Count);
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

        // Only drop rows this provider produced. Sibling providers (BlackFrame, Chromaprint,
        // EdlImport) also live in ChapterAnalysisResults under known sentinel MatchedChapterName
        // values; deleting them here would orphan their AnalysisStatus rows and silently strip
        // segments from items even though other providers still claim HasResults=true.
        db.ChapterAnalysisResults.RemoveRange(
            db.ChapterAnalysisResults.Where(r => r.ItemId == itemId
                && !ForeignSentinels.Contains(r.MatchedChapterName)));
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
            MediaSegmentType.Commercial => durationSeconds >= config.MinCommercialDurationSeconds
                                           && durationSeconds <= config.MaxCommercialDurationSeconds,
            _ => true
        };
    }

    internal static MediaSegmentType? MatchChapterName(Dictionary<MediaSegmentType, Regex[]> regexes, string chapterName)
    {
        // Defence in depth: a per-regex timeout already exists, but rejecting absurdly long
        // chapter titles before allocating regex state caps memory cost of crafted inputs.
        if (chapterName.Length > MaxChapterNameLength)
        {
            return null;
        }

        // A chapter name can match more than one type - e.g. "Opening Credits" hits both the
        // Intro keyword "Opening" and the Outro keyword "Credits". Resolve by:
        //   1. longest matched keyword first (the most specific match - this is what lets a
        //      multi-word literal like "Preview/Recap" win over a bare "Preview"), then
        //   2. earliest position in the title as a tie-break, so equally specific matches
        //      follow reading order ("the title leads with what it is") rather than the
        //      arbitrary type-registration order.
        MediaSegmentType? bestType = null;
        var bestLength = 0;
        var bestIndex = int.MaxValue;

        foreach (var (type, typeRegexes) in regexes)
        {
            foreach (var regex in typeRegexes)
            {
                try
                {
                    var match = regex.Match(chapterName);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var keyword = match.Groups[KeywordGroup];
                    if (keyword.Length > bestLength || (keyword.Length == bestLength && keyword.Index < bestIndex))
                    {
                        bestLength = keyword.Length;
                        bestIndex = keyword.Index;
                        bestType = type;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Malformed or adversarial chapter name - skip this regex
                }
            }
        }

        return bestType;
    }

    private MediaSegmentType? MatchChapterName(string chapterName) => MatchChapterName(_regexes, chapterName);

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        if (e is PluginConfiguration config)
        {
            _regexes = BuildRegexes(config);
        }
    }

    /// <summary>
    /// Builds compiled regexes for all segment types. Each pattern in a type's
    /// chapter-name list compiles to its own regex (wrapped in a word-boundary check
    /// so "Intro" matches "Intro" and "Intro:" but not "Introvert"). Multiple regexes
    /// per segment type are supported - a chapter matches a type if any of its
    /// regexes hits.
    /// </summary>
    /// <param name="config">The plugin configuration containing chapter name patterns.</param>
    /// <returns>A dictionary mapping segment types to their compiled regex arrays.</returns>
    internal static Dictionary<MediaSegmentType, Regex[]> BuildRegexes(PluginConfiguration config)
    {
        var regexes = new Dictionary<MediaSegmentType, Regex[]>();
        AddRegexes(regexes, MediaSegmentType.Intro, config.IntroChapterNames);
        AddRegexes(regexes, MediaSegmentType.Outro, config.OutroChapterNames);
        AddRegexes(regexes, MediaSegmentType.Recap, config.RecapChapterNames);
        AddRegexes(regexes, MediaSegmentType.Preview, config.PreviewChapterNames);
        AddRegexes(regexes, MediaSegmentType.Commercial, config.CommercialChapterNames);
        return regexes;
    }

    private static void AddRegexes(Dictionary<MediaSegmentType, Regex[]> regexes, MediaSegmentType type, string[] patterns)
    {
        if (patterns.Length == 0)
        {
            return;
        }

        var compiled = new Regex[patterns.Length];
        for (var i = 0; i < patterns.Length; i++)
        {
            var pattern = LeadingBoundary + @"(?<" + KeywordGroup + ">" + Regex.Escape(patterns[i]) + ")" + EndMarkerLookahead + TrailingBoundary;
            compiled[i] = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, _regexTimeout);
        }

        regexes[type] = compiled;
    }
}
