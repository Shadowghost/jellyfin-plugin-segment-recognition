using System;

namespace Jellyfin.Plugin.SegmentRecognition.Services;

/// <summary>
/// How much of an item is fingerprinted for intro matching.
/// </summary>
/// <remarks>
/// Deliberately not configurable. These decide only how much is searched on the <em>first</em>
/// pass: anything they miss is picked up by <see cref="ForRetry"/>, which the analysis task
/// applies to items whose season suggests their opening was cut off. 99% of matched intros end
/// within 22% of runtime, so the first pass is right nearly always and the retry stays rare.
/// </remarks>
public static class ChromaprintRegions
{
    /// <summary>
    /// Runtime at or below which the whole item is fingerprinted as the intro region. Short media
    /// has no room for a separate credits region worth comparing.
    /// </summary>
    public const double ShortMediaSeconds = 600;

    /// <summary>Fraction of runtime fingerprinted on the first pass.</summary>
    public const double IntroFraction = 0.25;

    /// <summary>Ceiling on the first-pass intro region, in seconds.</summary>
    public const double MaxIntroSeconds = 600;

    /// <summary>
    /// The first-pass intro region for an item, in seconds.
    /// </summary>
    /// <param name="runtimeSeconds">The item's runtime.</param>
    /// <returns>The region length.</returns>
    public static double FirstPass(double runtimeSeconds)
        => runtimeSeconds <= ShortMediaSeconds
            ? runtimeSeconds
            : Math.Min(runtimeSeconds * IntroFraction, MaxIntroSeconds);

    /// <summary>
    /// The intro region to retry with, in seconds: half the runtime.
    /// </summary>
    /// <remarks>
    /// Half, because the matcher rejects an intro starting past the midpoint anyway - so this
    /// searches everything still acceptable, and no more.
    /// </remarks>
    /// <param name="runtimeSeconds">The item's runtime.</param>
    /// <returns>The region length.</returns>
    public static double ForRetry(double runtimeSeconds) => runtimeSeconds / 2;
}
