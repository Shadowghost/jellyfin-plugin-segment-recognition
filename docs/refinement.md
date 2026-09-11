# Boundary refinement

Detection gives approximate boundaries. A fingerprint match starts where the shared audio starts,
which is not quite where the opening starts, and a fade to black covers a span rather than a point.
Refinement moves each boundary onto something the viewer will recognize as a clean cut.

It applies to black-frame and fingerprint results. Chapter-derived segments already sit on chapter
boundaries, and imported EDL segments are taken as given.

The refined boundaries are what gets stored and served.

## The three stages, in order

### 1. Silence

ffmpeg looks for silence around each boundary and moves it onto the nearest gap. Speech and music
rarely stop mid-word at the true edge of an opening, so a silence is good evidence of where one
part ends and another begins.

The search windows are asymmetric: 5 seconds towards the middle of the segment and 2 seconds away
from it. Detected boundaries tend to overshoot, so there is more room to pull a boundary in than to
push it out.

What counts as silence is set by **Silence Noise Floor** (-50 dB) and **Minimum Silence Duration**
(0.33 s). Detection results are cached in memory, since the same region is often examined more than
once.

### 2. Chapters

If the file has chapter markers, each boundary snaps to the nearest one within 5 seconds. A chapter
mark is an explicit statement about where something begins, so it outranks anything inferred.

Both boundaries move independently, which means they can collapse onto the same marker. That case
is detected and the snap discarded.

### 3. Keyframes

Finally each boundary moves onto a video keyframe, within 3 seconds. Players seek to keyframes, so
a boundary that sits between them makes the skip land somewhere slightly different from where the
segment says it ends.

The direction differs per boundary: a segment's start snaps to the keyframe at or before it, and
its end to the keyframe at or after it, so refinement never trims content off the inside of the
segment.

## The backstop

Every stage moves each boundary independently, and they all tend to move inward. Silence snapping
alone can pull each end up to 5 seconds towards the middle, which is enough to reduce a short
segment to almost nothing.

So the refined result is checked at the end: if it is inverted, or shorter than the minimum duration
that qualified the segment in the first place, the whole refinement is discarded and the raw
boundaries are stored instead.

The raw boundaries already satisfied whatever window admitted the segment. Refinement is an
improvement, not a requirement.

## Turning it off

Each stage has its own switch, all on by default. Disabling all three stores detection output
unchanged. See [Configuration](configuration.md).

Changing any refinement setting re-runs the cheap part of analysis: segments are recomputed from
cached detection data rather than re-extracted.
