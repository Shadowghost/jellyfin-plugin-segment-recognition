using System;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// Thrown when a query would have to push an unreasonable number of identifiers into a single
/// SQL parameter. The API surface maps this to <c>400 Bad Request</c> with the message intact so
/// the caller knows to narrow the scope rather than seeing a timeout or a multi-megabyte query.
/// </summary>
public class SegmentQueryLimitExceededException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentQueryLimitExceededException"/> class.
    /// </summary>
    public SegmentQueryLimitExceededException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentQueryLimitExceededException"/> class.
    /// </summary>
    /// <param name="message">The message describing the limit that was exceeded.</param>
    public SegmentQueryLimitExceededException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentQueryLimitExceededException"/> class.
    /// </summary>
    /// <param name="message">The message describing the limit that was exceeded.</param>
    /// <param name="innerException">The inner exception.</param>
    public SegmentQueryLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
