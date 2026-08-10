namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Lifecycle states a <see cref="JobEntry"/> moves through.
/// </summary>
public enum JobStatus
{
    /// <summary>The job has been submitted but the worker hasn't started yet.</summary>
    Pending,

    /// <summary>The worker is processing.</summary>
    Running,

    /// <summary>The worker finished cleanly.</summary>
    Completed,

    /// <summary>The worker stopped due to an error.</summary>
    Failed,

    /// <summary>The worker was cooperatively cancelled.</summary>
    Cancelled,
}
