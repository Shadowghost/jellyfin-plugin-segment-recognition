using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SegmentRecognition.Api.Dto;

/// <summary>
/// Request body for <c>POST /SegmentRecognition/v1/HasSegments</c>. The POST form avoids the
/// URL-length and proxy-caching pitfalls of the comma-separated GET variant while enforcing the
/// same per-request id cap server-side.
/// </summary>
public class HasSegmentsRequestDto
{
    /// <summary>
    /// Gets or sets the Jellyfin item identifiers to probe.
    /// </summary>
    public IReadOnlyList<Guid> Ids { get; set; } = [];
}
