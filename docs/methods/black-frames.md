# Black frames

**Off by default. Applies to episodes and movies.**

Television fades to black around its openings and before its credits. Those fades are visible in
the video itself, which makes them usable where there are no chapters and no siblings to compare
against, such as a movie or a single-episode season.

It is off by default because it is the only method that decodes video. Everything else reads
metadata or audio.

## 1. Where it looks

Only two regions are scanned, never the whole file:

| Region | Covers |
| --- | --- |
| Intro | from the start, 25% of the runtime or twice the maximum intro duration, whichever is shorter |
| Outro | the last 4 minutes, from **Outro Analysis Seconds** |

## 2. Finding the active picture

Before scanning, ffmpeg's crop detection runs on a 10 second sample to find the real picture area.
Letterbox bars are black in every frame, and without excluding them a scope-ratio film looks
permanently part-black. The result is cached per item and reused.

## 3. Scanning

ffmpeg reports, for each frame, what percentage of its pixels are black.

The scan is deliberately unfiltered. ffmpeg is asked to report *every* frame rather than only those
past a threshold, and the decision about what counts as black is made afterwards, where the whole
distribution is visible.

Frames are downscaled to **Analysis Resolution** (480p by default) first. Where hardware
acceleration is available the decode and the downscale both happen on the GPU, which is most of the
saving. See [Configuration](../configuration.md) for the hardware options.

How dark a single pixel must be to count as black is fixed, not a setting. It was once user-facing,
defaulting to a value at which a dark episode reported nearly every frame as black.

## 4. Deciding what counts as black

A fixed percentage assumes every source reaches the same baseline brightness. Dark-graded material
does not. When even the least-black frame of a scan is a third black pixels, a fixed bar lets
ordinary night scenes qualify as transitions.

So **Black Pixels Per Frame** (85% by default) is rescaled into the range the content actually
leaves available. The darkest 1% of the scan sets the floor, capped so that a region which is black
nearly end to end cannot push the bar up to where nothing qualifies at all. On material with real
white frames the floor is zero and the configured percentage applies unchanged.

Only the frames that pass are stored.

## 5. Clustering

Consecutive black frames within a second of each other form one cluster. Clusters shorter than
**Minimum Cluster Duration** (500 ms) are dropped, since a single dark frame is not a transition.

## 6. Choosing the segments

- **Intro:** the last cluster that ends within the intro duration window. The segment runs from the
  start of the file to the end of that cluster.
- **Outro:** the last cluster whose distance from the end of the file fits the outro window. The
  segment runs from the start of that cluster to the end of the file. Movies use their own, longer
  maximum.

Taking the last qualifying cluster means the latest fade that is still plausible, rather than the
first dark moment in the file.

The result then goes through [boundary refinement](../refinement.md).

## Re-analyzing

The percentage threshold decides which frames get stored, so changing it applies to newly analyzed
items only. Existing items keep the samples they already have.

To apply a change to everything already scanned, use **Re-analyze Black Frames**. It clears the
cached frames and scans again from scratch.

This is deliberate. A full re-scan is the most expensive thing the plugin can do, so it never
happens as a side effect of changing a setting. The cheaper half of the pipeline, clustering and
duration windows, does replay from the cached samples automatically when those settings change.

## When it finds nothing

Streaming-native shows often cut straight into their content with no fade at all. Heavily graded
material can also sit far enough from black that nothing crosses the bar. Neither is a failure, and
neither is fixed by lowering the threshold, which mostly adds false positives.
