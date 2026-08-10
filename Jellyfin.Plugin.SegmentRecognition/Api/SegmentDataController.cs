using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SegmentRecognition.Api.Dto;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.SegmentRecognition.Api;

/// <summary>
/// Admin-only HTTP API exposing the Segment Recognition plugin's per-provider analysis data.
/// All endpoints require <see cref="Policies.RequiresElevation"/>.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
[Route("SegmentRecognition/v1")]
[ClientAbortFilter]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class SegmentDataController : ControllerBase
{
    /// <summary>
    /// Base path this controller is routed at. Job URLs handed back in <c>Location</c> headers and
    /// <c>202</c> bodies are built from this so they stay in step with the <see cref="RouteAttribute"/>
    /// above rather than being spelled out (and drifting) at each call site.
    /// </summary>
    private const string RoutePrefix = "/SegmentRecognition/v1";

    /// <summary>
    /// Maximum number of IDs accepted by the bulk HasSegments endpoint.
    /// </summary>
    private const int HasSegmentsIdLimit = 200;

    /// <summary>
    /// Default page size for the analyzed-items listing.
    /// </summary>
    private const int DefaultPageSize = 50;

    /// <summary>
    /// Maximum page size for the analyzed-items listing.
    /// </summary>
    private const int MaxPageSize = 200;

    /// <summary>
    /// Maximum number of items accepted by the bulk recalculate request body.
    /// </summary>
    private const int BulkRecalculateLimit = 1000;

    /// <summary>
    /// Maximum number of leaf items a single recalculation request may expand to. A request naming
    /// one series can expand to thousands of episodes, so the cap is enforced after expansion as
    /// well as on the request itself — checking only the request would let a single id through.
    /// </summary>
    private const int BulkRecalculateLeafLimit = 5000;

    /// <summary>
    /// Bytes after which the comma-separated <c>HasSegments</c> id payload is rejected without parsing.
    /// 200 GUIDs of length 36 plus separators ≈ 7.5 KiB; we allow 16 KiB headroom.
    /// </summary>
    private const int HasSegmentsMaxQueryLength = 16 * 1024;

    /// <summary>
    /// Sort keys accepted by the analyzed-items listing.
    /// </summary>
    private static readonly string[] _itemOrderKeys = ["name", "lastAnalyzed"];

    /// <summary>
    /// Sort keys accepted by the segment search.
    /// </summary>
    private static readonly string[] _segmentOrderKeys = ["createdAt", "duration", "start"];

    /// <summary>
    /// Serializer options for WebSocket frames. Jellyfin configures MVC with
    /// <c>PropertyNamingPolicy = null</c>, so its REST bodies are PascalCase; the socket must use
    /// the same policy or the identical <see cref="JobDto"/> would arrive under different property
    /// names depending on the transport. Pinned explicitly rather than left to the serializer
    /// default so a future default change cannot silently split the two.
    /// </summary>
    private static readonly JsonSerializerOptions _streamJsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    private readonly IDbContextFactory<SegmentDbContext> _dbContextFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly ILogger<SegmentDataController> _logger;
    private readonly ChapterNameProvider _chapterNameProvider;
    private readonly BlackFrameProvider _blackFrameProvider;
    private readonly ChromaprintProvider _chromaprintProvider;
    private readonly RecalculationJobService _jobService;
    private readonly SegmentDataQueryService _queryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentDataController"/> class.
    /// </summary>
    /// <param name="dbContextFactory">Factory for the plugin's SQLite context.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="mediaSegmentManager">The Jellyfin media segment manager (used to push results).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="chapterNameProvider">The chapter-name provider.</param>
    /// <param name="blackFrameProvider">The black-frame provider.</param>
    /// <param name="chromaprintProvider">The chromaprint provider.</param>
    /// <param name="jobService">The async job orchestrator.</param>
    /// <param name="queryService">The read-side query and DTO-assembly service.</param>
    public SegmentDataController(
        IDbContextFactory<SegmentDbContext> dbContextFactory,
        ILibraryManager libraryManager,
        IMediaSegmentManager mediaSegmentManager,
        ILogger<SegmentDataController> logger,
        ChapterNameProvider chapterNameProvider,
        BlackFrameProvider blackFrameProvider,
        ChromaprintProvider chromaprintProvider,
        RecalculationJobService jobService,
        SegmentDataQueryService queryService)
    {
        _dbContextFactory = dbContextFactory;
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _logger = logger;
        _chapterNameProvider = chapterNameProvider;
        _blackFrameProvider = blackFrameProvider;
        _chromaprintProvider = chromaprintProvider;
        _jobService = jobService;
        _queryService = queryService;
    }

    /// <summary>
    /// Returns the full per-provider analysis data the plugin has stored for the given item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The aggregated data.</returns>
    [HttpGet("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemSegmentDataDto>> GetItemData(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var result = await _queryService.GetItemDataAsync(itemId, cancellationToken).ConfigureAwait(false);

        // Matches HEAD on the same route, which 404s an item the plugin has never touched.
        // Returning 200 with an empty body made the two disagree about whether the item exists.
        if (!result.HasAnyData)
        {
            return NotFound();
        }

        if (TryConditionalGet(result.Watermark, result.RowCount, out var notModified))
        {
            return notModified!;
        }

        return Ok(result.Dto);
    }

    /// <summary>
    /// Returns what each of this plugin's providers would hand Jellyfin right now, calling
    /// <see cref="IMediaSegmentProvider.GetMediaSegments"/> on each. This is the "reshaped"
    /// result: every provider serves its stored, refinement-adjusted rows. Black-frame segments
    /// fall back to re-clustering the raw samples only for items last analyzed by a build that
    /// did not yet persist refined boundaries.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Per-provider segment lists.</returns>
    [HttpGet("Items/{itemId}/ProviderSegments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemProviderSegmentsDto>> GetProviderSegments(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var dto = await _queryService.GetProviderSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Queues a recalculation as a background job and returns its descriptor immediately.
    /// </summary>
    /// <param name="request">The recalculation request body.</param>
    /// <returns>The job descriptor.</returns>
    [HttpPost("Recalculate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<JobDto> StartRecalculateJob([FromBody] RecalculateRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var itemIds = ResolveRequestItemIds(request);
        if (itemIds.Count == 0)
        {
            return Problem(detail: "No items to recalculate.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (itemIds.Count > BulkRecalculateLimit)
        {
            return Problem(detail: $"At most {BulkRecalculateLimit} items per request.", statusCode: StatusCodes.Status400BadRequest);
        }

        var leaves = new List<BaseItem>();
        var skipped = new List<Guid>();
        foreach (var id in itemIds)
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                skipped.Add(id);
                continue;
            }

            if (TryExpandToLeaves(item, out var expanded, out _))
            {
                leaves.AddRange(expanded);
            }
            else
            {
                // Resolved to something we can't analyze (e.g. a folder); report it back.
                skipped.Add(id);
            }
        }

        // Version expansion can yield duplicates when a request names both a primary
        // and one of its alternate versions.
        leaves = leaves.DistinctBy(l => l.Id).ToList();

        if (leaves.Count == 0)
        {
            return Problem(detail: "Resolved zero leaf items (Movies/Episodes) from the request.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (leaves.Count > BulkRecalculateLeafLimit)
        {
            return Problem(
                detail: $"The request expands to {leaves.Count} items; at most {BulkRecalculateLeafLimit} can be recalculated per request.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var clearCache = request.ClearCache;
        var parallelism = RecalculationJobService.ResolveParallelism(request.MaxParallelism);
        var firstLeaf = leaves[0];
        var leafCount = leaves.Count;
        var initialMessage = leafCount == 1
            ? $"Recalculating {firstLeaf.Name}"
            : $"Recalculating {leafCount} items ({firstLeaf.Name}, …)";

        // Idempotency guard: refuse a second identical recalc while one is still active so the
        // same work doesn't run twice concurrently. The lookup and the registration happen under
        // one lock inside the service; testing first and submitting after would let two identical
        // requests that arrive together both pass.
        var submitted = _jobService.TrySubmit(
            kind: "Recalculate",
            targetIds: itemIds,
            totalLeaves: leafCount,
            work: async (entry, ct) =>
            {
                entry.Message = initialMessage;
                _jobService.Notify(entry);

                await ExecuteRecalculateAsync(leaves, clearCache, parallelism, entry, ct).ConfigureAwait(false);

                entry.Message = $"Recalculated {leafCount - entry.FailedLeaves}/{leafCount} items.";
            },
            job: out var dto,
            skippedIds: skipped);

        if (!submitted)
        {
            return Conflict(new ProblemDetails
            {
                Title = "An identical recalculation is already in progress.",
                Detail = $"Job {dto.JobId} is already processing these items.",
                Status = StatusCodes.Status409Conflict,
                Extensions = { ["jobId"] = dto.JobId, ["jobUrl"] = JobUrl(dto.JobId) },
            });
        }

        Response.Headers["Location"] = JobUrl(dto.JobId);
        return Accepted(JobUrl(dto.JobId), dto);
    }

    /// <summary>
    /// Runs <see cref="ChromaprintProvider.AnalyzeGroupAsync"/> on demand for a single group
    /// (season for TV, album for audio). Useful when a previous scheduled task was cancelled
    /// before reaching the group-comparison phase and the group is stuck without
    /// chromaprint-derived segments.
    /// </summary>
    /// <param name="groupId">The group identifier (season or album).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Rematch summary.</returns>
    [HttpPost("Groups/{groupId}/Rematch")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<JobDto>> RematchGroup(
        [FromRoute, Required] Guid groupId,
        CancellationToken cancellationToken = default)
    {
        int fingerprintedCount;
        using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            fingerprintedCount = await db.ChromaprintResults
                .AsNoTracking()
                .Where(r => r.SeasonId == groupId)
                .Select(r => r.ItemId)
                .Distinct()
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (fingerprintedCount == 0)
        {
            return NotFound();
        }

        _logger.LogInformation("Manual rematch requested for group {GroupId}", groupId);

        // A rematch is CPU-bound and can run for minutes, so it runs as a background job (streamable
        // via the same Jobs endpoints as Recalculate) rather than blocking the request. Refuse a
        // second concurrent rematch of the same group.
        var submitted = _jobService.TrySubmit(
            kind: "Rematch",
            targetIds: new[] { groupId },
            totalLeaves: 1,
            work: async (entry, ct) =>
            {
                entry.Bump("fingerprintedItems", fingerprintedCount);
                entry.Message = $"Rematching {fingerprintedCount} fingerprinted items…";
                _jobService.Notify(entry);

                await _chromaprintProvider.AnalyzeGroupAsync(groupId, ct).ConfigureAwait(false);

                using var summaryDb = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var fingerprintedItemIds = await summaryDb.ChromaprintResults
                    .AsNoTracking()
                    .Where(r => r.SeasonId == groupId)
                    .Select(r => r.ItemId)
                    .Distinct()
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                // Analysis only rewrites the plugin's own cache. Jellyfin keeps serving whatever it
                // already holds until the providers are asked again, so a rematch that is not
                // followed by a push changes nothing a player can see. Every fingerprinted item in
                // the group is pushed, including the ones the rematch left with no segment at all:
                // for those the push is the only thing that clears what Jellyfin still has.
                foreach (var itemId in fingerprintedItemIds)
                {
                    ct.ThrowIfCancellationRequested();

                    var item = _libraryManager.GetItemById(itemId);
                    if (item is null)
                    {
                        continue;
                    }

                    try
                    {
                        await _mediaSegmentManager.RunSegmentPluginProviders(
                            item,
                            _libraryManager.GetLibraryOptions(item),
                            forceOverwrite: true,
                            ct).ConfigureAwait(false);
                        entry.Bump("segmentsPushed");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
#pragma warning disable CA1031 // continue pushing other items on a single push failure
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        _logger.LogWarning(ex, "Failed to push segments for \"{ItemName}\" after rematch", item.Name);
                    }
                }

                var itemsWithResults = await summaryDb.AnalysisStatuses
                    .AsNoTracking()
                    .CountAsync(
                        s => s.ProviderName == ProviderNames.Chromaprint
                            && s.HasResults
                            && fingerprintedItemIds.Contains(s.ItemId),
                        ct)
                    .ConfigureAwait(false);

                entry.Bump("itemsWithResults", itemsWithResults);
                entry.IncrementCompleted();
                entry.Message = $"Rematched group: {itemsWithResults}/{fingerprintedItemIds.Count} items have chromaprint segments.";
            },
            job: out var dto);

        if (!submitted)
        {
            return Conflict(new ProblemDetails
            {
                Title = "A rematch is already in progress for this group.",
                Status = StatusCodes.Status409Conflict,
                Extensions = { ["jobId"] = dto.JobId, ["jobUrl"] = JobUrl(dto.JobId) },
            });
        }

        Response.Headers["Location"] = JobUrl(dto.JobId);
        return Accepted(JobUrl(dto.JobId), dto);
    }

    /// <summary>
    /// Returns analyzed items, optionally constrained to a given Jellyfin library/folder/series
    /// and filtered by analysis state, provider, or modification time.
    /// </summary>
    /// <param name="parentId">Optional parent identifier; only descendants are returned when set.</param>
    /// <param name="hasSegments">When set, restricts to items where any provider produced (true) or did not produce (false) results.</param>
    /// <param name="provider">When set, restricts to items analyzed by the given provider name.</param>
    /// <param name="analyzedSince">When set, restricts to items most recently analyzed at or after this UTC instant.</param>
    /// <param name="hasError">When set, restricts to containers where some provider does (true) or does not (false) have a stored <see cref="AnalysisStatus.LastError"/>. Evaluated on the rolled-up container, so a series with one failing provider still reports its other providers' segments.</param>
    /// <param name="orderBy">"name" (default) or "lastAnalyzed". Anything else is rejected.</param>
    /// <param name="descending">Reverses the sort.</param>
    /// <param name="startIndex">Number of results to skip.</param>
    /// <param name="limit">Maximum number of results to return (clamped to <see cref="MaxPageSize"/>).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A paged response.</returns>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AnalyzedItemsResponseDto>> GetAnalyzedItems(
        [FromQuery] Guid? parentId,
        [FromQuery] bool? hasSegments,
        [FromQuery] string? provider,
        [FromQuery] DateTime? analyzedSince,
        [FromQuery] bool? hasError,
        [FromQuery] string? orderBy,
        [FromQuery] bool descending = false,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        limit = Math.Clamp(limit, 1, MaxPageSize);

        if (!TryValidateOrderBy(orderBy, _itemOrderKeys, out var orderByError))
        {
            return Problem(detail: orderByError, statusCode: StatusCodes.Status400BadRequest);
        }

        var response = await _queryService.GetAnalyzedItemsAsync(
            new AnalyzedItemsQuery
            {
                ParentId = parentId,
                HasSegments = hasSegments,
                Provider = provider,
                AnalyzedSince = analyzedSince,
                HasError = hasError,
                OrderBy = orderBy,
                Descending = descending,
                StartIndex = startIndex,
                Limit = limit,
            },
            cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    /// <summary>
    /// Bulk existence check used by the SPA to badge poster grids.
    /// </summary>
    /// <param name="ids">Comma-separated list of Jellyfin item identifiers (max <see cref="HasSegmentsIdLimit"/>).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One entry per requested ID.</returns>
    [HttpGet("HasSegments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<HasSegmentsResultDto>>> GetHasSegments(
        [FromQuery, Required] string ids,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ids))
        {
            return Ok(Array.Empty<HasSegmentsResultDto>());
        }

        if (ids.Length > HasSegmentsMaxQueryLength)
        {
            return Problem(
                detail: $"ids parameter exceeds {HasSegmentsMaxQueryLength} bytes; cap is {HasSegmentsIdLimit} GUIDs per request.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tokens = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Reject on the token count rather than on how many of them happened to parse. Capping
        // after the parse silently answered for a subset of an over-long list, and the caller
        // could not tell a dropped id from an unanalyzed one.
        if (tokens.Length > HasSegmentsIdLimit)
        {
            return Problem(detail: $"At most {HasSegmentsIdLimit} ids per request; got {tokens.Length}.", statusCode: StatusCodes.Status400BadRequest);
        }

        var parsed = tokens
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToArray();

        if (parsed.Length == 0)
        {
            return Ok(Array.Empty<HasSegmentsResultDto>());
        }

        var results = await _queryService.GetHasSegmentsAsync(parsed, cancellationToken).ConfigureAwait(false);
        Response.Headers["Cache-Control"] = "private, max-age=10";
        return Ok(results);
    }

    /// <summary>
    /// Bulk existence check via a JSON body. Preferred over the <c>GET</c> variant for large id
    /// sets: it sidesteps URL-length and proxy-caching limits while enforcing the same cap.
    /// </summary>
    /// <param name="request">The request body containing the item identifiers.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One entry per requested ID.</returns>
    [HttpPost("HasSegments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<HasSegmentsResultDto>>> PostHasSegments(
        [FromBody] HasSegmentsRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parsed = (request.Ids ?? [])
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToArray();

        if (parsed.Length == 0)
        {
            return Ok(Array.Empty<HasSegmentsResultDto>());
        }

        if (parsed.Length > HasSegmentsIdLimit)
        {
            return Problem(detail: $"At most {HasSegmentsIdLimit} ids per request.", statusCode: StatusCodes.Status400BadRequest);
        }

        var results = await _queryService.GetHasSegmentsAsync(parsed, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }

    /// <summary>
    /// Lists this plugin's segment providers, their display labels, and whether each is currently
    /// enabled. Lets clients drive provider filters and legends without hard-coding names or
    /// reaching into the core plugin configuration endpoint.
    /// </summary>
    /// <returns>The provider descriptors.</returns>
    [HttpGet("Providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ProviderInfoDto>> GetProviders()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var providers = new[]
        {
            new ProviderInfoDto { Name = ProviderNames.ChapterName, DisplayName = "Chapter names", Enabled = config.EnableChapterNameProvider },
            new ProviderInfoDto { Name = ProviderNames.BlackFrame, DisplayName = "Black frames", Enabled = config.EnableBlackFrameProvider },
            new ProviderInfoDto { Name = ProviderNames.Chromaprint, DisplayName = "Chromaprint", Enabled = config.EnableChromaprintProvider },
            new ProviderInfoDto { Name = ProviderNames.EdlImport, DisplayName = "EDL import", Enabled = config.EnableEdlImportProvider },
        };
        return Ok(providers);
    }

    /// <summary>
    /// Lightweight existence probe for a single item. <c>200</c> when at least one provider
    /// has stored results; <c>204</c> when analyzed but no results; <c>404</c> when never
    /// analyzed. Useful for ETag-style polling.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An empty response with the appropriate status code.</returns>
    [HttpHead("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> HeadItem(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var presence = await _queryService.GetItemPresenceAsync(itemId, cancellationToken).ConfigureAwait(false);
        return presence switch
        {
            ItemPresence.HasResults => Ok(),
            ItemPresence.AnalyzedNoResults => NoContent(),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// Removes all stored analysis data for the given item across every provider, and re-runs the
    /// providers against Jellyfin so the now-empty result replaces any segments this plugin had
    /// already pushed there.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The deletion summary.</returns>
    [HttpDelete("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeleteResultDto>> DeleteItemData(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var rowsRemoved = 0;

        // Take the same per-item lock a recalculation takes. Deleting rows out from under an
        // in-flight analysis of the same item leaves half-written results behind.
        var ran = await _jobService.RunWithLockAsync(
            RecalculationJobService.ItemLockKey(itemId),
            async () => rowsRemoved = await _queryService.DeleteItemDataAsync(itemId, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (!ran)
        {
            return Conflict(new ProblemDetails
            {
                Title = "This item is currently being analyzed.",
                Detail = "Wait for the in-flight job to finish, or cancel it, before deleting its data.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        if (rowsRemoved == 0)
        {
            return NotFound();
        }

        // Deleting our rows does not retract what Jellyfin already stored, so the player would
        // keep offering skip buttons for segments the plugin no longer has. Re-running the
        // providers with forceOverwrite replaces them with the (now empty) result.
        if (_libraryManager.GetItemById(itemId) is { } item)
        {
            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                await _mediaSegmentManager.RunSegmentPluginProviders(item, libraryOptions, forceOverwrite: true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // the rows are gone either way; surfacing the delete result matters more
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Cleared stored data for item {ItemId} but could not refresh its Jellyfin segments", itemId);
            }
        }

        return Ok(new DeleteResultDto { ItemId = itemId, RowsRemoved = rowsRemoved });
    }

    /// <summary>
    /// Searches stored segments across the library by type and/or duration. Combines chapter,
    /// chromaprint-derived, and EDL rows (all stored as <see cref="ChapterAnalysisResult"/>).
    /// </summary>
    /// <param name="type">Segment type filter (enum name; case-insensitive).</param>
    /// <param name="minDurationMs">Minimum duration in milliseconds.</param>
    /// <param name="maxDurationMs">Maximum duration in milliseconds.</param>
    /// <param name="parentId">Optional parent identifier to constrain the search.</param>
    /// <param name="source">Case-insensitive substring the matched source/chapter name must contain. Matched literally: <c>%</c> and <c>_</c> are not wildcards.</param>
    /// <param name="orderBy">"createdAt" (default), "duration", or "start". Anything else is rejected.</param>
    /// <param name="descending">Reverses the sort. Default order is newest-first by creation time.</param>
    /// <param name="startIndex">Number of rows to skip.</param>
    /// <param name="limit">Page size (clamped to <see cref="MaxPageSize"/>).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The matching rows.</returns>
    [HttpGet("Segments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SegmentSearchResponseDto>> SearchSegments(
        [FromQuery] string? type,
        [FromQuery] long? minDurationMs,
        [FromQuery] long? maxDurationMs,
        [FromQuery] Guid? parentId,
        [FromQuery] string? source,
        [FromQuery] string? orderBy,
        [FromQuery] bool descending = false,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        limit = Math.Clamp(limit, 1, MaxPageSize);

        int? typeValue = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<MediaSegmentType>(type, true, out var parsed))
            {
                return Problem(detail: $"Unknown segment type '{type}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            typeValue = (int)parsed;
        }

        if (!TryValidateOrderBy(orderBy, _segmentOrderKeys, out var orderByError))
        {
            return Problem(detail: orderByError, statusCode: StatusCodes.Status400BadRequest);
        }

        // Durations are converted to ticks (×10 000) before they reach SQL, so an unchecked
        // multiplication would wrap a large value negative and invert the comparison.
        if (!TryValidateDuration(minDurationMs, nameof(minDurationMs), out var durationError)
            || !TryValidateDuration(maxDurationMs, nameof(maxDurationMs), out durationError))
        {
            return Problem(detail: durationError, statusCode: StatusCodes.Status400BadRequest);
        }

        SegmentSearchResponseDto response;
        try
        {
            response = await _queryService.SearchSegmentsAsync(
                new SegmentSearchQuery
                {
                    TypeValue = typeValue,
                    MinDurationMs = minDurationMs,
                    MaxDurationMs = maxDurationMs,
                    ParentId = parentId,
                    NameContains = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
                    OrderBy = orderBy,
                    Descending = descending,
                    StartIndex = startIndex,
                    Limit = limit,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SegmentQueryLimitExceededException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        return Ok(response);
    }

    /// <summary>
    /// Returns aggregate statistics across all analyzed items.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Aggregate stats.</returns>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SegmentStatsDto>> GetStats(CancellationToken cancellationToken = default)
    {
        var result = await _queryService.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        Response.Headers["Cache-Control"] = "private, max-age=10";
        if (TryConditionalGet(result.Watermark, result.RowCount, out var notModified))
        {
            return notModified!;
        }

        return Ok(result.Dto);
    }

    /// <summary>
    /// Lists every job currently tracked by the in-memory registry, newest-first.
    /// </summary>
    /// <returns>Job snapshots.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<JobDto>> ListJobs()
    {
        return Ok(_jobService.ListJobs());
    }

    /// <summary>
    /// Returns the latest snapshot of a single job.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>The job snapshot.</returns>
    [HttpGet("Jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<JobDto> GetJob([FromRoute, Required] Guid jobId)
    {
        var dto = _jobService.GetJob(jobId);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Requests cooperative cancellation of an in-flight job.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>The job snapshot.</returns>
    [HttpDelete("Jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<JobDto> CancelJob([FromRoute, Required] Guid jobId)
    {
        if (!_jobService.CancelJob(jobId))
        {
            return NotFound();
        }

        var dto = _jobService.GetJob(jobId);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Streams progress updates for a job over a WebSocket. Each frame is a JSON-serialized
    /// <see cref="JobDto"/> snapshot; the connection closes after a terminal status frame.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="cancellationToken">A cancellation token tied to the request lifetime.</param>
    /// <returns>A task that completes when the socket is closed.</returns>
    [HttpGet("Jobs/{jobId}/Stream")]
    [ProducesResponseType(StatusCodes.Status101SwitchingProtocols)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StreamJob(
        [FromRoute, Required] Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return Problem(detail: "This endpoint requires a WebSocket upgrade.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (_jobService.GetJob(jobId) is null)
        {
            return NotFound();
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await StreamSnapshotsAsync(socket, jobId, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    private async Task StreamSnapshotsAsync(WebSocket socket, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in _jobService.SubscribeAsync(jobId, cancellationToken).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, _streamJsonOptions);
                await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }

            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "job ended", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket dropped during job stream {JobId}", jobId);
        }
    }

    private async Task ExecuteRecalculateAsync(
        IReadOnlyList<BaseItem> leaves,
        bool clearCache,
        int parallelism,
        JobEntry jobEntry,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var groupIdsTouched = new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>();

        using var sem = new SemaphoreSlim(parallelism);
        var tasks = leaves.Select(async leaf =>
        {
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Serialize per item. Two overlapping requests - say one for a series and one for
                // an episode inside it - both pass the exact-target-set idempotency check and would
                // otherwise clean up and re-insert the same rows concurrently.
                var ran = await _jobService.RunWithLockAsync(
                    RecalculationJobService.ItemLockKey(leaf.Id),
                    () => AnalyzeLeafAsync(leaf, config, clearCache, groupIdsTouched, jobEntry, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (!ran)
                {
                    _logger.LogDebug("Skipping item {ItemId}: another job is already analyzing it", leaf.Id);
                    jobEntry.Bump("skippedLocked");
                }

                jobEntry.IncrementCompleted();
                _jobService.Notify(jobEntry);
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Group cross-match runs once per touched group (sequential — chromaprint is CPU-bound).
        foreach (var groupId in groupIdsTouched.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _chromaprintProvider.AnalyzeGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
            jobEntry.Bump("groupsCrossMatched");
            _jobService.Notify(jobEntry);
        }

        // Push every leaf's refreshed segments to Jellyfin so the player picks them up.
        foreach (var leaf in leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(leaf);
                await _mediaSegmentManager.RunSegmentPluginProviders(leaf, libraryOptions, forceOverwrite: true, cancellationToken).ConfigureAwait(false);
                jobEntry.Bump("segmentsPushed");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // continue pushing other leaves on a single push failure
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Push failed for item {ItemId}", leaf.Id);
            }
        }
    }

    private async Task AnalyzeLeafAsync(
        BaseItem leaf,
        PluginConfiguration config,
        bool clearCache,
        System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> groupIdsTouched,
        JobEntry jobEntry,
        CancellationToken cancellationToken)
    {
        var leafFailed = false;
        var containerId = AnalysisGrouping.GetContainerId(leaf);

        if (config.EnableChapterNameProvider)
        {
            leafFailed |= !await RunProviderAsync(
                leaf.Id,
                containerId,
                ProviderNames.ChapterName,
                async () =>
                {
                    if (clearCache)
                    {
                        await _chapterNameProvider.CleanupExtractedData(leaf.Id, cancellationToken).ConfigureAwait(false);
                    }

                    await _chapterNameProvider.AnalyzeAsync(leaf.Id, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            jobEntry.Bump("chapterAnalyzed");
        }

        if (config.EnableBlackFrameProvider)
        {
            leafFailed |= !await RunProviderAsync(
                leaf.Id,
                containerId,
                ProviderNames.BlackFrame,
                async () =>
                {
                    if (clearCache)
                    {
                        await _blackFrameProvider.CleanupExtractedData(leaf.Id, cancellationToken).ConfigureAwait(false);
                    }

                    await _blackFrameProvider.AnalyzeAsync(leaf.Id, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            jobEntry.Bump("blackFrameAnalyzed");
        }

        var groupId = ChromaprintProvider.GetGroupId(leaf);
        if (config.EnableChromaprintProvider && groupId != Guid.Empty)
        {
            leafFailed |= !await RunProviderAsync(
                leaf.Id,
                containerId,
                ProviderNames.Chromaprint,
                async () =>
                {
                    if (clearCache)
                    {
                        await _chromaprintProvider.CleanupExtractedData(leaf.Id, cancellationToken).ConfigureAwait(false);
                    }

                    await _chromaprintProvider.GenerateFingerprintAsync(leaf.Id, SegmentSourceNames.RegionIntro, cancellationToken).ConfigureAwait(false);
                    jobEntry.Bump("fingerprintsGenerated");

                    if (config.EnableCreditsFingerprinting)
                    {
                        await _chromaprintProvider.GenerateFingerprintAsync(leaf.Id, SegmentSourceNames.RegionCredits, cancellationToken).ConfigureAwait(false);
                        jobEntry.Bump("fingerprintsGenerated");
                    }
                },
                cancellationToken).ConfigureAwait(false);

            groupIdsTouched.TryAdd(groupId, 0);
        }

        if (leafFailed)
        {
            jobEntry.IncrementFailed();
        }
    }

    private async Task<bool> RunProviderAsync(Guid itemId, Guid containerId, string providerName, Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(false);
            await _jobService.ClearProviderErrorAsync(itemId, providerName, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // capture provider failure per leaf
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Provider {Provider} failed for item {ItemId}", providerName, itemId);
            await _jobService.RecordProviderErrorAsync(
                itemId,
                providerName,
                containerId,
                RecalculationJobService.SanitizeMessage(ex),
                cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private bool TryExpandToLeaves(BaseItem item, out List<BaseItem> leaves, out string? rejection)
    {
        leaves = new List<BaseItem>();
        rejection = null;
        if (item is Season || item is Series)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [item.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                // Alternate versions are excluded by default; analysis is per file.
                IncludeOwnedItems = true,
            });
            foreach (var ep in episodes)
            {
                leaves.Add(ep);
            }

            return true;
        }

        if (item is Episode or Movie)
        {
            // Analysis is per media file, so a grouped video expands to every version.
            leaves.AddRange(ExpandVersions((Video)item));
            return true;
        }

        rejection = $"Recalculate is only supported for Movie, Episode, Season, or Series items; got {item.GetType().Name}.";
        return false;
    }

    /// <summary>
    /// Returns the given video together with every alternate version grouped with it
    /// (local and linked). When the video is itself an alternate version, the group is
    /// expanded from its primary so all siblings are covered.
    /// </summary>
    private IEnumerable<BaseItem> ExpandVersions(Video video)
    {
        if (video.PrimaryVersionId is { } primaryId
            && primaryId != Guid.Empty
            && _libraryManager.GetItemById(primaryId) is Video primary)
        {
            video = primary;
        }

        yield return video;

        foreach (var id in _libraryManager.GetLocalAlternateVersionIds(video))
        {
            if (_libraryManager.GetItemById(id) is Video local)
            {
                yield return local;
            }
        }

        foreach (var linked in _libraryManager.GetLinkedAlternateVersions(video))
        {
            yield return linked;
        }
    }

    private List<Guid> ResolveRequestItemIds(RecalculateRequestDto request)
    {
        var ids = new HashSet<Guid>();
        if (request.ItemIds is not null)
        {
            foreach (var id in request.ItemIds)
            {
                if (id != Guid.Empty)
                {
                    ids.Add(id);
                }
            }
        }

        if (request.ParentId is { } parentId && parentId != Guid.Empty)
        {
            var parent = _libraryManager.GetItemById(parentId);
            if (parent is not null)
            {
                var descendants = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = [parentId],
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                    Recursive = true,
                    // Alternate versions are excluded by default; analysis is per file.
                    IncludeOwnedItems = true,
                });
                foreach (var descendant in descendants)
                {
                    ids.Add(descendant.Id);
                }
            }
        }

        return ids.ToList();
    }

    private static string JobUrl(Guid jobId) => $"{RoutePrefix}/Jobs/{jobId}";

    /// <summary>
    /// Rejects an unrecognized sort key instead of silently falling back to the default order,
    /// which made a typo look like a working request that ignored the caller's intent.
    /// </summary>
    private static bool TryValidateOrderBy(string? orderBy, string[] allowed, out string? error)
    {
        if (string.IsNullOrWhiteSpace(orderBy)
            || allowed.Contains(orderBy.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            error = null;
            return true;
        }

        error = $"Unknown orderBy '{orderBy}'. Supported values: {string.Join(", ", allowed)}.";
        return false;
    }

    private static bool TryValidateDuration(long? value, string name, out string? error)
    {
        const long MaxMs = long.MaxValue / TimeSpan.TicksPerMillisecond;
        if (value is not { } ms)
        {
            error = null;
            return true;
        }

        if (ms < 0)
        {
            error = $"{name} must not be negative.";
            return false;
        }

        if (ms > MaxMs)
        {
            error = $"{name} must be at most {MaxMs}.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Emits the ETag for the current representation and reports whether the request's
    /// <c>If-None-Match</c> already matches it. The header is a comma-separated list and its
    /// entries may be weak, so it is parsed rather than compared whole.
    /// </summary>
    private bool TryConditionalGet(DateTime? watermark, int rowCount, out ActionResult? notModified)
    {
        var key = $"{watermark?.Ticks ?? 0L}:{rowCount}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        var etag = "\"" + Convert.ToHexString(hash[..8]).ToLowerInvariant() + "\"";

        Response.Headers["ETag"] = etag;
        if (Request.Headers.TryGetValue("If-None-Match", out var inm) && MatchesEtag(inm, etag))
        {
            notModified = new StatusCodeResult(StatusCodes.Status304NotModified);
            return true;
        }

        notModified = null;
        return false;
    }

    private static bool MatchesEtag(StringValues ifNoneMatch, string etag)
    {
        foreach (var header in ifNoneMatch)
        {
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            foreach (var candidate in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (candidate == "*")
                {
                    return true;
                }

                // The comparison is weak (RFC 9110 §13.1.2): a weak validator still identifies the
                // same representation for the purposes of a conditional GET.
                var value = candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate;
                if (string.Equals(value, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
