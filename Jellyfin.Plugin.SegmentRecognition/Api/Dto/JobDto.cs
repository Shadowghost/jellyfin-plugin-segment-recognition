using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Snapshot of an asynchronous job (recalculate / rematch) produced by
/// <see cref="Services.RecalculationJobService"/>. The shape is intentionally stable so the
/// SPA can poll a single endpoint regardless of what the job is doing.
/// </summary>
public class JobDto
{
    /// <summary>
    /// Gets or sets the job identifier.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Gets or sets the job kind ("Recalculate" or "Rematch").
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the job status ("Pending", "Running", "Completed", "Failed", "Cancelled").
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the queue position when status is "Pending". Null for terminal statuses.
    /// </summary>
    public int? QueuePosition { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the job was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when work began.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when work finished (or was cancelled).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the total number of leaves to process.
    /// </summary>
    public int TotalLeaves { get; set; }

    /// <summary>
    /// Gets or sets the number of leaves the worker has finished (success or skip).
    /// </summary>
    public int CompletedLeaves { get; set; }

    /// <summary>
    /// Gets or sets the number of leaves that failed (one or more providers threw).
    /// </summary>
    public int FailedLeaves { get; set; }

    /// <summary>
    /// Gets or sets the cumulative work counters as a free-form bag of named totals. Keys are
    /// job-kind specific (e.g. a Recalculate job reports <c>chapterAnalyzed</c>,
    /// <c>fingerprintsGenerated</c>, …; a Rematch job reports <c>itemsWithResults</c>). Keeping
    /// this a dictionary decouples the stable job envelope from any single job kind's shape.
    /// </summary>
    public IReadOnlyDictionary<string, long> Counters { get; set; } = new Dictionary<string, long>();

    /// <summary>
    /// Gets or sets a top-level error message (e.g. job-wide failure). Null on partial / per-leaf
    /// errors — those are recorded against <see cref="Data.Entities.AnalysisStatus"/> rows.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the human-readable summary line set when the job ends.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets identifiers attached to the job for context (item IDs, group IDs).
    /// </summary>
    public IReadOnlyList<Guid> TargetIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the requested identifiers that could not be resolved to any library item and
    /// were dropped before the job started. Empty when everything resolved.
    /// </summary>
    public IReadOnlyList<Guid> SkippedIds { get; set; } = [];
}
