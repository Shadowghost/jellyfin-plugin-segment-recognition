using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Api.Dto;
using Jellyfin.Plugin.SegmentRecognition.Api.Mappers;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Owns the read-side query and DTO-assembly logic behind the plugin's HTTP API: database
/// access, Jellyfin <see cref="ILibraryManager"/> joins, and aggregation. The controller is
/// left with HTTP concerns only (status codes, ETag/304, headers, validation).
/// </summary>
public sealed class SegmentDataQueryService
{
    /// <summary>
    /// Maximum number of black-frame rows returned per item. Items can have hundreds, and the
    /// SPA shows them in a collapsible section anyway.
    /// </summary>
    private const int BlackFrameRowCap = 500;

    /// <summary>
    /// Number of descendant identifiers pushed into a single <c>json_each</c> parameter. The
    /// analyzed-items aggregate is split into chunks of this size and merged in memory, so a
    /// library-root filter never builds a multi-megabyte query parameter.
    /// </summary>
    private const int ParentIdChunkSize = 10_000;

    /// <summary>
    /// Number of container identifiers checked against the library per round trip when paging the
    /// analyzed-items listing in timestamp order. Also the floor on the window size, so a small
    /// page still resolves in one call.
    /// </summary>
    private const int ResolutionWindowSize = 200;

    /// <summary>
    /// Upper bound on the descendant set the segment search will scope itself to. Unlike the
    /// analyzed-items aggregate, an ordered+paged search cannot be split into chunks and merged,
    /// so past this size the caller is asked to narrow the scope instead.
    /// </summary>
    private const int MaxSearchParentDescendants = 50_000;

    /// <summary>
    /// Escape character used with <c>LIKE</c> so user-supplied <c>%</c> and <c>_</c> are matched
    /// literally instead of acting as wildcards.
    /// </summary>
    private const string LikeEscapeCharacter = "\\";

    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<SegmentDataQueryService> _logger;
    private readonly IReadOnlyList<IMediaSegmentProvider> _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentDataQueryService"/> class.
    /// </summary>
    /// <param name="dbContextFactory">Factory for the plugin's SQLite context.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="imageProcessor">The Jellyfin image processor (used for primary-image cache tags).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="chapterNameProvider">The chapter-name provider.</param>
    /// <param name="blackFrameProvider">The black-frame provider.</param>
    /// <param name="chromaprintProvider">The chromaprint provider.</param>
    /// <param name="edlImportProvider">The EDL-import provider.</param>
    public SegmentDataQueryService(
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILibraryManager libraryManager,
        IImageProcessor imageProcessor,
        ILogger<SegmentDataQueryService> logger,
        ChapterNameProvider chapterNameProvider,
        BlackFrameProvider blackFrameProvider,
        ChromaprintProvider chromaprintProvider,
        EdlImportProvider edlImportProvider)
    {
        _dbContextFactory = dbContextFactory;
        _libraryManager = libraryManager;
        _imageProcessor = imageProcessor;
        _logger = logger;

        // Deliberately injected by concrete type: we only want this plugin's providers,
        // not every IMediaSegmentProvider registered by other plugins.
        _providers = new IMediaSegmentProvider[]
        {
            chapterNameProvider,
            blackFrameProvider,
            chromaprintProvider,
            edlImportProvider,
        };
    }

    /// <summary>
    /// Converts a millisecond duration to ticks without overflowing. Values beyond the tick range
    /// saturate at <see cref="long.MaxValue"/>, which keeps a "minimum duration" filter meaning
    /// "longer than anything that exists" instead of silently wrapping to a negative bound that
    /// matches every row.
    /// </summary>
    /// <param name="milliseconds">The duration in milliseconds.</param>
    /// <returns>The equivalent tick count, saturated at the bounds of <see cref="long"/>.</returns>
    public static long MillisecondsToTicksSaturating(long milliseconds)
    {
        const long MaxMs = long.MaxValue / TimeSpan.TicksPerMillisecond;
        if (milliseconds >= MaxMs)
        {
            return long.MaxValue;
        }

        if (milliseconds <= -MaxMs)
        {
            return long.MinValue;
        }

        return milliseconds * TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// Returns the full per-provider analysis data stored for the given item, along with the
    /// values the caller needs to compute a conditional-GET ETag.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The aggregated data and its ETag inputs.</returns>
    public async Task<ItemSegmentDataResult> GetItemDataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // Five independent reads run in parallel, each on its own short-lived context.
        var statusesTask = QueryAsync(
            db => db.AnalysisStatuses
                .AsNoTracking()
                .Where(s => s.ItemId == itemId)
                .ToListAsync(cancellationToken),
            cancellationToken);
        var chaptersTask = QueryAsync(
            db => db.ChapterAnalysisResults
                .AsNoTracking()
                .Where(r => r.ItemId == itemId)
                .OrderBy(r => r.StartTicks)
                .ThenBy(r => r.Id)
                .ToListAsync(cancellationToken),
            cancellationToken);

        // One row past the cap so the response can say whether it was truncated.
        var blackFramesTask = QueryAsync(
            db => db.BlackFrameResults
                .AsNoTracking()
                .Where(r => r.ItemId == itemId)
                .OrderBy(r => r.TimestampTicks)
                .Take(BlackFrameRowCap + 1)
                .ToListAsync(cancellationToken),
            cancellationToken);
        var chromaprintsTask = QueryAsync(
            db => db.ChromaprintResults
                .AsNoTracking()
                .Where(r => r.ItemId == itemId)
                .ToListAsync(cancellationToken),
            cancellationToken);
        var cropTask = QueryAsync(
            db => db.CropDetectResults
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ItemId == itemId, cancellationToken),
            cancellationToken);

        var statuses = await statusesTask.ConfigureAwait(false);
        var chapters = await chaptersTask.ConfigureAwait(false);
        var blackFrames = await blackFramesTask.ConfigureAwait(false);
        var chromaprints = await chromaprintsTask.ConfigureAwait(false);
        var crop = await cropTask.ConfigureAwait(false);

        var blackFramesTruncated = blackFrames.Count > BlackFrameRowCap;
        if (blackFramesTruncated)
        {
            blackFrames.RemoveAt(blackFrames.Count - 1);
        }

        var dto = new ItemSegmentDataDto
        {
            ItemId = itemId,
            AnalysisStatuses = statuses.Select(SegmentDtoMapper.ToDto).ToArray(),
            ChapterResults = chapters.Select(SegmentDtoMapper.ToDto).ToArray(),
            BlackFrames = blackFrames.Select(SegmentDtoMapper.ToDto).ToArray(),
            BlackFramesTruncated = blackFramesTruncated,
            ChromaprintResults = chromaprints.Select(SegmentDtoMapper.ToDto).ToArray(),
            CropDetect = crop is null ? null : SegmentDtoMapper.ToDto(crop),
        };

        // Errors are written without touching AnalyzedAt, so both timestamps feed the watermark.
        var watermark = statuses
            .SelectMany(s => new[] { (DateTime?)s.AnalyzedAt, s.LastErrorAt })
            .DefaultIfEmpty(null)
            .Max();

        // Count every table the DTO draws from: a provider that rewrites result rows without
        // changing its status row would otherwise reuse the previous ETag.
        var rowCount = statuses.Count
            + chapters.Count
            + blackFrames.Count
            + chromaprints.Count
            + (crop is null ? 0 : 1);

        return new ItemSegmentDataResult(dto, SegmentDtoMapper.AsUtc(watermark), rowCount, rowCount > 0);
    }

    /// <summary>
    /// Returns what each of this plugin's providers would hand Jellyfin right now, calling
    /// <see cref="IMediaSegmentProvider.GetMediaSegments"/> on each. Returns <c>null</c> when the
    /// item does not exist.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Per-provider segment lists, or <c>null</c> when the item is unknown.</returns>
    public async Task<ItemProviderSegmentsDto?> GetProviderSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var request = new MediaSegmentGenerationRequest
        {
            ItemId = itemId,
            ExistingSegments = [],
        };

        var results = new List<ProviderSegmentsDto>(_providers.Count);
        foreach (var provider in _providers)
        {
            var entry = new ProviderSegmentsDto { ProviderName = provider.Name };
            try
            {
                var supported = await provider.Supports(item).ConfigureAwait(false);
                entry.Supported = supported;
                if (supported)
                {
                    var segments = await provider.GetMediaSegments(request, cancellationToken).ConfigureAwait(false);
                    entry.Segments = segments
                        .Select(s => new SegmentDto
                        {
                            Type = SegmentDtoMapper.SegmentTypeName((int)s.Type),
                            StartMs = SegmentDtoMapper.TicksToMilliseconds(s.StartTicks),
                            EndMs = SegmentDtoMapper.TicksToMilliseconds(s.EndTicks),
                        })
                        .ToArray();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Do not catch general exception types - one provider shouldn't kill the whole response
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Provider {Provider} threw while producing segments for item {ItemId}", provider.Name, itemId);
                entry.Error = RecalculationJobService.SanitizeMessage(ex);
            }

            results.Add(entry);
        }

        return new ItemProviderSegmentsDto { ItemId = itemId, Providers = results };
    }

    /// <summary>
    /// Returns analyzed items, optionally constrained to a Jellyfin parent and filtered by
    /// analysis state, provider, or modification time. Results are rolled up to the container
    /// level (series for episodes; the item itself for movies).
    /// </summary>
    /// <param name="query">The normalized query parameters.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A paged response.</returns>
    public async Task<AnalyzedItemsResponseDto> GetAnalyzedItemsAsync(AnalyzedItemsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<Guid>? descendantIds = null;
        if (query.ParentId is { } parentId && parentId != Guid.Empty)
        {
            var parent = _libraryManager.GetItemById(parentId);
            if (parent is null)
            {
                return new AnalyzedItemsResponseDto();
            }

            descendantIds = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                AncestorIds = [parentId],
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                // Alternate versions are excluded by default, but their analysis rows are
                // stored per version item id and must survive the parent filter.
                IncludeOwnedItems = true,
            });
            if (descendantIds is null || descendantIds.Count == 0)
            {
                return new AnalyzedItemsResponseDto();
            }
        }

        var containerAgg = await AggregateContainersAsync(query, descendantIds, cancellationToken).ConfigureAwait(false);
        if (containerAgg.Count == 0)
        {
            return new AnalyzedItemsResponseDto();
        }

        // Both the has-results and has-error predicates are applied here rather than in SQL.
        // They are properties of the rolled-up container, not of an individual provider row: a
        // series whose chapter provider succeeded and whose chromaprint provider failed has both
        // segments and an error, and filtering rows before the roll-up would report neither
        // correctly. Applying them post-merge also keeps chunked parent queries consistent.
        var filtered = containerAgg.Values.Where(a =>
            (query.HasSegments is not { } wantSegments || a.HasResults == wantSegments)
            && (query.HasError is not { } wantError || a.HasError == wantError));

        var matched = filtered.ToDictionary(a => a.ItemId);
        if (matched.Count == 0)
        {
            return new AnalyzedItemsResponseDto();
        }

        // Whichever ordering is asked for, only the requested page is ever resolved against the
        // library. The count and the page are still derived from the same predicate, so they cannot
        // disagree the way they did when the count came from the plugin DB and the page from the
        // library: containers that no longer resolve (a deleted series whose rows are still cached)
        // were counted but never returned, and the last page came back short or empty.
        var byLastAnalyzed = string.Equals(query.OrderBy, "lastAnalyzed", StringComparison.OrdinalIgnoreCase);
        var sortOrder = query.Descending ? SortOrder.Descending : SortOrder.Ascending;
        var matchedIds = matched.Keys.ToArray();

        int totalRecordCount;
        IReadOnlyList<BaseItem> pagedItems;

        // Non-null only for the lastAnalyzed order, where the library cannot reproduce the
        // ordering and it has to be re-imposed on the page it hands back.
        Guid[]? explicitOrdering = null;

        if (byLastAnalyzed)
        {
            // Ties on the timestamp are broken by id so paging stays stable across requests.
            var ordered = query.Descending
                ? matched.Values.OrderByDescending(a => a.LastAnalyzedAt).ThenByDescending(a => a.ItemId)
                : matched.Values.OrderBy(a => a.LastAnalyzedAt).ThenBy(a => a.ItemId);

            totalRecordCount = _libraryManager.GetCount(new InternalItemsQuery
            {
                ItemIds = matchedIds,
                // Containers are normally primaries, but rows written before the version
                // rollup existed can still point at an alternate version; don't drop them.
                IncludeOwnedItems = true,
            });

            var pageIds = ResolvePageInOrder(
                ordered.Select(a => a.ItemId),
                query.StartIndex,
                query.Limit,
                window => _libraryManager.GetItemIds(new InternalItemsQuery
                {
                    ItemIds = window,
                    IncludeOwnedItems = true,
                }),
                cancellationToken);
            if (pageIds.Length == 0)
            {
                return new AnalyzedItemsResponseDto { TotalRecordCount = totalRecordCount };
            }

            pagedItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ItemIds = pageIds,
                IncludeOwnedItems = true,
            });

            explicitOrdering = pageIds;
        }
        else
        {
            // Name ordering is the library's job, and so are the paging and the count: one query
            // sorts, offsets, limits and counts in SQL instead of dragging every matched id back
            // into managed memory to be sorted and sliced here.
            var result = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                ItemIds = matchedIds,
                OrderBy = new[] { (ItemSortBy.SortName, sortOrder) },
                StartIndex = query.StartIndex,
                Limit = query.Limit,
                EnableTotalRecordCount = true,
                IncludeOwnedItems = true,
            });

            totalRecordCount = result.TotalRecordCount;
            pagedItems = result.Items;
        }

        if (pagedItems is null || pagedItems.Count == 0)
        {
            return new AnalyzedItemsResponseDto { TotalRecordCount = totalRecordCount };
        }

        // The dictionary is filled by indexer rather than ToDictionary: a repeated id in the
        // library's answer would otherwise throw.
        var itemsById = new Dictionary<Guid, BaseItem>(pagedItems.Count);
        foreach (var item in pagedItems)
        {
            if (item is not null)
            {
                itemsById[item.Id] = item;
            }
        }

        var ordering = explicitOrdering
            ?? pagedItems.Where(i => i is not null).Select(i => i.Id).Distinct().ToArray();

        var page = new List<AnalyzedItemSummaryDto>(ordering.Length);
        foreach (var id in ordering)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (itemsById.TryGetValue(id, out var item) && matched.TryGetValue(id, out var entry))
            {
                page.Add(BuildSummary(item, entry));
            }
        }

        return new AnalyzedItemsResponseDto
        {
            Items = page,
            TotalRecordCount = totalRecordCount,
        };
    }

    /// <summary>
    /// Walks an already-ordered id sequence and returns the requested slice, keeping only ids the
    /// library still resolves. Ids are checked a window at a time and the walk stops as soon as the
    /// page is full, so a first page costs one round trip over a window rather than one over every
    /// matched container.
    /// </summary>
    /// <remarks>
    /// Internal, and taking the library lookup as a delegate, so the windowing and the skip/take
    /// arithmetic can be exercised without standing up an <see cref="ILibraryManager"/>.
    /// </remarks>
    /// <param name="orderedIds">The candidate ids, already in the caller's sort order.</param>
    /// <param name="startIndex">Number of resolvable ids to skip.</param>
    /// <param name="limit">Maximum number of ids to return.</param>
    /// <param name="resolve">Returns the subset of a window that the library still knows about.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The resolvable ids for the requested page, in order.</returns>
    internal static Guid[] ResolvePageInOrder(
        IEnumerable<Guid> orderedIds,
        int startIndex,
        int limit,
        Func<Guid[], IReadOnlyList<Guid>?> resolve,
        CancellationToken cancellationToken)
    {
        var needed = startIndex + limit;
        var alive = new List<Guid>(Math.Min(needed, ResolutionWindowSize));

        // Rows outliving their item are rare, so a window sized to the page almost always fills it
        // on the first pass; the floor keeps small pages from making one round trip per handful.
        var windowSize = Math.Max(needed, ResolutionWindowSize);

        foreach (var window in orderedIds.Chunk(windowSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolved = resolve(window);

            if (resolved is { Count: > 0 })
            {
                var resolvable = new HashSet<Guid>(resolved);
                foreach (var id in window)
                {
                    if (resolvable.Contains(id))
                    {
                        alive.Add(id);
                    }
                }
            }

            if (alive.Count >= needed)
            {
                break;
            }
        }

        if (alive.Count <= startIndex)
        {
            return Array.Empty<Guid>();
        }

        return alive.Skip(startIndex).Take(limit).ToArray();
    }

    /// <summary>
    /// Bulk existence check used by the SPA to badge poster grids.
    /// </summary>
    /// <param name="ids">The parsed, de-duplicated item identifiers.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One entry per requested ID, in the order supplied.</returns>
    public async Task<IReadOnlyList<HasSegmentsResultDto>> GetHasSegmentsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return Array.Empty<HasSegmentsResultDto>();
        }

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var statuses = await db.AnalysisStatuses
            .AsNoTracking()
            .Where(s => ids.Contains(s.ItemId))
            .Select(s => new { s.ItemId, s.ProviderName, s.HasResults })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var analyzedItems = statuses.Select(s => s.ItemId).ToHashSet();
        var providersByItem = statuses
            .Where(s => s.HasResults)
            .GroupBy(r => r.ItemId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ProviderName).ToArray());

        return ids.Select(id =>
        {
            providersByItem.TryGetValue(id, out var providers);
            return new HasSegmentsResultDto
            {
                ItemId = id,
                HasSegments = providers is { Length: > 0 },
                Analyzed = analyzedItems.Contains(id),
                Providers = providers ?? [],
            };
        }).ToArray();
    }

    /// <summary>
    /// Lightweight existence probe for a single item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Whether the item was analyzed and whether any provider stored results.</returns>
    public async Task<ItemPresence> GetItemPresenceAsync(Guid itemId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var hasAny = await db.AnalysisStatuses
            .AsNoTracking()
            .Where(s => s.ItemId == itemId)
            .Select(s => new { s.HasResults })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (hasAny.Count == 0)
        {
            return ItemPresence.NotAnalyzed;
        }

        return hasAny.Any(s => s.HasResults) ? ItemPresence.HasResults : ItemPresence.AnalyzedNoResults;
    }

    /// <summary>
    /// Removes all stored analysis data for the given item across every provider, atomically.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows removed across all tables.</returns>
    public async Task<int> DeleteItemDataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // One transaction across all five tables: a failure partway through would otherwise leave
        // the status row deleted but result rows behind (or the reverse), which reads as "never
        // analyzed" while still serving stale segments.
        using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var rowsRemoved = 0;
        rowsRemoved += await db.AnalysisStatuses.Where(s => s.ItemId == itemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        rowsRemoved += await db.ChapterAnalysisResults.Where(r => r.ItemId == itemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        rowsRemoved += await db.BlackFrameResults.Where(r => r.ItemId == itemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        rowsRemoved += await db.ChromaprintResults.Where(r => r.ItemId == itemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        rowsRemoved += await db.CropDetectResults.Where(r => r.ItemId == itemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return rowsRemoved;
    }

    /// <summary>
    /// Searches stored segments across the library by type and/or duration.
    /// </summary>
    /// <param name="query">The normalized search parameters.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The matching rows.</returns>
    /// <exception cref="SegmentQueryLimitExceededException">
    /// Thrown when <see cref="SegmentSearchQuery.ParentId"/> resolves to more descendants than the
    /// search can scope itself to in one query.
    /// </exception>
    public async Task<SegmentSearchResponseDto> SearchSegmentsAsync(SegmentSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyCollection<Guid>? allowedIds = null;
        if (query.ParentId is { } parentId && parentId != Guid.Empty)
        {
            var parent = _libraryManager.GetItemById(parentId);
            if (parent is null)
            {
                return new SegmentSearchResponseDto();
            }

            var descendantIds = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                AncestorIds = [parentId],
                // Only these kinds ever carry segment rows; restricting here keeps the id set
                // (and therefore the query parameter) an order of magnitude smaller than pulling
                // every descendant including seasons and folders.
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                // Alternate versions are excluded by default, but segment rows are stored
                // per version item id and must survive the parent filter.
                IncludeOwnedItems = true,
            });
            allowedIds = descendantIds is { Count: > 0 } ? descendantIds : [];
            if (allowedIds.Count == 0)
            {
                return new SegmentSearchResponseDto();
            }

            if (allowedIds.Count > MaxSearchParentDescendants)
            {
                throw new SegmentQueryLimitExceededException(string.Format(
                    CultureInfo.InvariantCulture,
                    "parentId resolves to {0} items, more than the {1} this search can scope to. Narrow it to a series, season, or smaller folder.",
                    allowedIds.Count,
                    MaxSearchParentDescendants));
            }
        }

        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var q = db.ChapterAnalysisResults.AsNoTracking().AsQueryable();
        if (query.TypeValue is { } typeValue)
        {
            q = q.Where(r => r.SegmentType == typeValue);
        }

        if (query.MinDurationMs is { } minDurationMs)
        {
            var minTicks = MillisecondsToTicksSaturating(minDurationMs);
            q = q.Where(r => r.EndTicks - r.StartTicks >= minTicks);
        }

        if (query.MaxDurationMs is { } maxDurationMs)
        {
            var maxTicks = MillisecondsToTicksSaturating(maxDurationMs);
            q = q.Where(r => r.EndTicks - r.StartTicks <= maxTicks);
        }

        if (allowedIds is not null)
        {
            q = q.Where(r => allowedIds.Contains(r.ItemId));
        }

        if (!string.IsNullOrWhiteSpace(query.NameContains))
        {
            // Escape LIKE metacharacters so searching for "50%" means the literal text.
            var needle = query.NameContains
                .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter, StringComparison.Ordinal)
                .Replace("%", LikeEscapeCharacter + "%", StringComparison.Ordinal)
                .Replace("_", LikeEscapeCharacter + "_", StringComparison.Ordinal);
            q = q.Where(r => r.MatchedChapterName != null
                && EF.Functions.Like(r.MatchedChapterName, "%" + needle + "%", LikeEscapeCharacter));
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ApplySegmentOrder(q, query.OrderBy, query.Descending)
            .Skip(query.StartIndex)
            .Take(query.Limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Select(r => new SegmentSearchResultDto
        {
            ItemId = r.ItemId,
            Source = r.MatchedChapterName,
            Type = SegmentDtoMapper.SegmentTypeName(r.SegmentType),
            StartMs = SegmentDtoMapper.TicksToMilliseconds(r.StartTicks),
            EndMs = SegmentDtoMapper.TicksToMilliseconds(r.EndTicks),
            CreatedAt = SegmentDtoMapper.AsUtc(r.CreatedAt),
        }).ToArray();

        return new SegmentSearchResponseDto
        {
            Items = items,
            TotalRecordCount = total,
        };
    }

    /// <summary>
    /// Returns aggregate statistics across all analyzed items, along with the values the caller
    /// needs to compute a conditional-GET ETag.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Aggregate stats and their ETag inputs.</returns>
    public async Task<SegmentStatsResult> GetStatsAsync(CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Aggregate DB-side via translatable LINQ (no provider-specific SQL). The status table is
        // keyed on (ItemId, ProviderName), so within a provider each item appears at most once —
        // that makes "distinct items per provider" a plain COUNT, which EF Core can translate
        // (a per-group COUNT(DISTINCT) cannot be). This emits a single GROUP BY query.
        var perProviderRaw = await db.AnalysisStatuses
            .AsNoTracking()
            .GroupBy(s => s.ProviderName)
            .Select(g => new
            {
                Name = g.Key,
                AnalyzedItems = g.Count(),
                ItemsWithResults = g.Count(s => s.HasResults),
                LastAnalyzedAt = g.Max(s => (DateTime?)s.AnalyzedAt),
                LastErrorAt = g.Max(s => s.LastErrorAt),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var perProvider = perProviderRaw
            .Select(p => new ProviderStatsDto
            {
                Name = p.Name,
                AnalyzedItems = p.AnalyzedItems,
                ItemsWithResults = p.ItemsWithResults,
                LastAnalyzedAt = SegmentDtoMapper.AsUtc(p.LastAnalyzedAt),
            })
            .ToList();

        perProvider.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        // An item spans multiple providers, so the library-wide total does need a DISTINCT — but
        // over the whole table, which EF Core translates as COUNT over a DISTINCT subquery.
        var totalAnalyzedItems = await db.AnalysisStatuses
            .AsNoTracking()
            .Select(s => s.ItemId)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        // ETag inputs derive from the per-provider aggregate with no extra round-trip: because the
        // key is (ItemId, ProviderName), total rows == sum of per-provider counts, and the overall
        // watermark is the max of the per-provider watermarks. Error timestamps are folded in for
        // the same reason as in GetItemDataAsync: recording one does not move AnalyzedAt.
        var rowCount = perProviderRaw.Sum(p => p.AnalyzedItems);
        var watermark = perProviderRaw
            .SelectMany(p => new[] { p.LastAnalyzedAt, p.LastErrorAt })
            .DefaultIfEmpty(null)
            .Max();

        var dto = new SegmentStatsDto
        {
            TotalAnalyzedItems = totalAnalyzedItems,
            PerProvider = perProvider.ToArray(),
        };

        return new SegmentStatsResult(dto, SegmentDtoMapper.AsUtc(watermark), rowCount);
    }

    /// <summary>
    /// Applies the requested sort to a segment-search query. With no <paramref name="orderBy"/> the
    /// default is newest-first by creation time (preserving legacy behaviour); when a key is given,
    /// <paramref name="descending"/> selects the direction (ascending by default). Every sort is
    /// tie-broken on the surrogate key: the sort columns are all non-unique (a bulk insert shares
    /// one <c>CreatedAt</c> to the tick), and without a total order SQLite is free to return ties
    /// differently per query, which duplicates and drops rows across pages.
    /// </summary>
    /// <param name="q">The query to order.</param>
    /// <param name="orderBy">The requested sort key, or <c>null</c> for the default.</param>
    /// <param name="descending">Whether to reverse the sort.</param>
    /// <returns>The ordered query.</returns>
    internal static IQueryable<ChapterAnalysisResult> ApplySegmentOrder(IQueryable<ChapterAnalysisResult> q, string? orderBy, bool descending)
    {
        switch (orderBy?.Trim().ToLowerInvariant())
        {
            case "duration":
                return descending
                    ? q.OrderByDescending(r => r.EndTicks - r.StartTicks).ThenByDescending(r => r.Id)
                    : q.OrderBy(r => r.EndTicks - r.StartTicks).ThenBy(r => r.Id);
            case "start":
                return descending
                    ? q.OrderByDescending(r => r.StartTicks).ThenByDescending(r => r.Id)
                    : q.OrderBy(r => r.StartTicks).ThenBy(r => r.Id);
            case "createdat":
                return descending
                    ? q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                    : q.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id);
            default:
                return q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
        }
    }

    /// <summary>
    /// Runs the container roll-up aggregate, splitting a parent filter across several
    /// <c>json_each</c> batches and merging them in memory. MAX, boolean OR and set-union are all
    /// associative, so a merged result is identical to what a single unbatched query would return.
    /// </summary>
    private async Task<Dictionary<Guid, ItemAggregate>> AggregateContainersAsync(
        AnalyzedItemsQuery query,
        IReadOnlyList<Guid>? descendantIds,
        CancellationToken cancellationToken)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var providerFilter = string.IsNullOrWhiteSpace(query.Provider) ? null : query.Provider;

        // Model binding yields Kind=Unspecified for "2026-08-01T00:00:00". Treating that as local
        // (which ToUniversalTime does) would shift the bound by the server's offset against
        // timestamps that are always stored in UTC.
        DateTime? analyzedSinceUtc = SegmentDtoMapper.AsUtc(query.AnalyzedSince);

        var merged = new Dictionary<Guid, ItemAggregate>();
        if (descendantIds is null)
        {
            foreach (var row in await RunAggregateAsync(db, null, providerFilter, analyzedSinceUtc, cancellationToken).ConfigureAwait(false))
            {
                Merge(merged, row);
            }

            return merged;
        }

        foreach (var chunk in descendantIds.Chunk(ParentIdChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // EF Core's Sqlite provider stores Guid as uppercase hyphenated TEXT (verified against
            // the live segments.db), but the join is written COLLATE NOCASE so a row written in a
            // different casing still matches instead of silently disappearing from the listing.
            var json = JsonSerializer.Serialize(chunk.Select(id => id.ToString("D").ToUpperInvariant()));
            foreach (var row in await RunAggregateAsync(db, json, providerFilter, analyzedSinceUtc, cancellationToken).ConfigureAwait(false))
            {
                Merge(merged, row);
            }
        }

        return merged;
    }

    /// <summary>
    /// Runs one container roll-up over the status table. Internal so the raw SQL - which EF cannot
    /// type-check for us - is exercised directly by tests.
    /// </summary>
    /// <param name="db">The context to query.</param>
    /// <param name="allowedIdsJson">A JSON array of leaf item ids to restrict to, or <c>null</c> for the whole table.</param>
    /// <param name="providerFilter">A provider name to restrict to, or <c>null</c> for all providers.</param>
    /// <param name="analyzedSinceUtc">A lower bound on the analysis instant, or <c>null</c> for no bound.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One row per container.</returns>
    internal static async Task<List<AggregateRow>> RunAggregateAsync(
        SegmentDbContext db,
        string? allowedIdsJson,
        string? providerFilter,
        DateTime? analyzedSinceUtc,
        CancellationToken cancellationToken)
    {
        var fromClause = allowedIdsJson is null
            ? "FROM AnalysisStatuses s"
            : "FROM AnalysisStatuses s INNER JOIN json_each({0}) a ON a.value = s.ItemId COLLATE NOCASE";

        // Roll up to the container level (series for episodes, the item itself otherwise) entirely
        // in SQL via the stored ContainerId. Rows still awaiting backfill carry Guid.Empty and are
        // excluded so they don't collapse into a bogus "empty" container. The json_each join (when
        // a parent is given) filters leaves by ItemId; grouping then happens on ContainerId.
        var rawSql = $@"
            SELECT
              s.ContainerId AS ItemId,
              MAX(s.AnalyzedAt) AS LastAnalyzedAtRaw,
              MAX(CASE WHEN s.HasResults = 1 THEN 1 ELSE 0 END) AS HasResultsInt,
              MAX(CASE WHEN s.LastError IS NOT NULL THEN 1 ELSE 0 END) AS HasErrorInt,
              group_concat(CASE WHEN s.HasResults = 1 THEN s.ProviderName END, '|') AS ProvidersConcat
            {fromClause}
            WHERE
              s.ContainerId <> '00000000-0000-0000-0000-000000000000'
              AND ({{1}} IS NULL OR s.ProviderName = {{1}})
              AND ({{2}} IS NULL OR s.AnalyzedAt >= {{2}})
            GROUP BY s.ContainerId";

        var rawParams = new object[]
        {
            (object?)allowedIdsJson ?? DBNull.Value,
            (object?)providerFilter ?? DBNull.Value,
            (object?)analyzedSinceUtc ?? DBNull.Value,
        };

        return await db.Database
            .SqlQueryRaw<AggregateRow>(rawSql, rawParams)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void Merge(Dictionary<Guid, ItemAggregate> into, AggregateRow row)
    {
        var providers = string.IsNullOrEmpty(row.ProvidersConcat)
            ? Array.Empty<string>()
            : row.ProvidersConcat.Split('|', StringSplitOptions.RemoveEmptyEntries);

        if (!into.TryGetValue(row.ItemId, out var existing))
        {
            into[row.ItemId] = new ItemAggregate
            {
                ItemId = row.ItemId,
                LastAnalyzedAt = SegmentDtoMapper.AsUtc(row.LastAnalyzedAtRaw),
                HasResults = row.HasResultsInt == 1,
                HasError = row.HasErrorInt == 1,
                Providers = new HashSet<string>(providers, StringComparer.Ordinal),
            };
            return;
        }

        var candidate = SegmentDtoMapper.AsUtc(row.LastAnalyzedAtRaw);
        if (candidate > existing.LastAnalyzedAt || existing.LastAnalyzedAt is null)
        {
            existing.LastAnalyzedAt = candidate;
        }

        existing.HasResults |= row.HasResultsInt == 1;
        existing.HasError |= row.HasErrorInt == 1;
        foreach (var provider in providers)
        {
            existing.Providers.Add(provider);
        }
    }

    private AnalyzedItemSummaryDto BuildSummary(BaseItem item, ItemAggregate entry)
    {
        string? seriesName = null;
        int? seasonNumber = null;
        int? episodeNumber = null;

        if (item is Episode episode)
        {
            seriesName = episode.SeriesName;
            seasonNumber = episode.ParentIndexNumber;
            episodeNumber = episode.IndexNumber;
        }

        string? primaryTag = null;
        try
        {
            primaryTag = _imageProcessor.GetImageCacheTag(item, ImageType.Primary);
        }
        catch (Exception ex)
        {
            // Image processor can throw for items without resolvable image paths; not fatal.
            _logger.LogDebug(ex, "Could not resolve primary image cache tag for item {ItemId}", item.Id);
        }

        return new AnalyzedItemSummaryDto
        {
            ItemId = item.Id,
            Name = item.Name ?? string.Empty,
            ItemType = item.GetType().Name,
            SeriesName = seriesName,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            ProductionYear = item.ProductionYear,
            PrimaryImageTag = primaryTag,
            HasSegments = entry.HasResults,
            HasError = entry.HasError,
            LastAnalyzedAt = entry.LastAnalyzedAt,
            Providers = entry.Providers.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
        };
    }

    private async Task<TResult> QueryAsync<TResult>(Func<SegmentDbContext, Task<TResult>> action, CancellationToken cancellationToken)
    {
        var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(db).ConfigureAwait(false);
        }
        finally
        {
            await db.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Materialization target for the raw aggregation query. SQLite returns ints for
    /// the boolean MAX() expressions, so we re-derive the bool in C#.
    /// </summary>
    internal sealed class AggregateRow
    {
        public Guid ItemId { get; set; }

        public DateTime? LastAnalyzedAtRaw { get; set; }

        public int HasResultsInt { get; set; }

        public int HasErrorInt { get; set; }

        public string? ProvidersConcat { get; set; }
    }

    private sealed class ItemAggregate
    {
        public Guid ItemId { get; set; }

        public DateTime? LastAnalyzedAt { get; set; }

        public bool HasResults { get; set; }

        public bool HasError { get; set; }

        public HashSet<string> Providers { get; set; } = new(StringComparer.Ordinal);
    }
}
