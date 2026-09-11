# Configuration

**Dashboard > Plugins > Segment Recognition**, or the *Segment Recognition* entry in the main menu.

The last column of each table says what happens to work already done when the setting changes:

- **Re-runs** means affected items are analyzed again on the next task run, automatically.
- **Recomputes** means the stored data is reused and only the conclusions are drawn again. Cheap.
- **New items only** means existing results keep the old setting until something forces them to be
  redone.

## Providers

| Setting | Default | Effect | On change |
| --- | --- | --- | --- |
| Chapter Name | On | [Chapter name matching](methods/chapter-names.md) | Re-runs |
| Chromaprint | On | [Audio fingerprinting](methods/audio-fingerprinting.md), episodes only | Re-runs |
| EDL Import | On | [Reads `.edl` sidecars](methods/edl.md) on query | Immediate |
| Black Frame | Off | [Black frame detection](methods/black-frames.md) | Re-runs |

Sub-options of audio fingerprinting:

| Setting | Default | Effect | On change |
| --- | --- | --- | --- |
| Credits Fingerprinting | On | Also fingerprint the end of the file to find closing credits | Re-runs |
| Season Outlier Pruning | On | Discard segments at a position almost no other episode shares | Recomputes |
| Preview Inference | Off | Turn the tail after the credits into a Preview segment | Recomputes |

## Duration limits

A detected segment is discarded unless it fits the window for its type. These are the main defence
against false positives, so widening them widens what gets accepted.

| Setting | Default | Applies to |
| --- | --- | --- |
| Min / Max Intro | 5 s / 240 s | Intro, and the lower bound for Recap and Preview |
| Min / Max Outro | 15 s / 600 s | Outro, and the upper bound for Recap |
| Max Movie Outro | 900 s | Outro on movies, which have longer credits |
| Min / Max Commercial | 5 s / 180 s | Commercial |
| Min / Max Preview | 10 s / 45 s | Preview inference only |

All of these re-run the analysis they affect.

## Analysis region

| Setting | Default | Effect | On change |
| --- | --- | --- | --- |
| Outro Analysis Seconds | 240 s | How far back from the end black-frame detection scans | Recomputes, see below |
| Credits Analysis Duration | 240 s | How much of the end is fingerprinted | Re-runs |
| Probe Audio Duration | On | Anchor the credits region to the audio rather than the container runtime | Re-runs |

The intro region is not configurable. It adapts per season, as described in
[Audio fingerprinting](methods/audio-fingerprinting.md).

Widening **Outro Analysis Seconds** recomputes segments from the frames already cached, which were
scanned over the old, narrower region. To actually scan the wider region on items already analyzed,
use **Re-analyze Black Frames**.

## Black frame

| Setting | Default | Effect | On change |
| --- | --- | --- | --- |
| Analysis Resolution | 480p | Frames are downscaled to this height before scanning. 480p, 720p or native | New items only |
| Black Pixels Per Frame | 85% | How much of a frame must be black, normalized against how dark the scan is | New items only |
| Minimum Cluster Duration | 500 ms | Shortest run of black frames that counts as a transition | Recomputes |
| Re-analyze Black Frames | Off | One-shot: clear every cached frame and scan again | Re-runs everything |

The first two decide which frames are stored, which is why they apply to new items only.
**Re-analyze Black Frames** is the escape hatch, and it clears itself once the run that honours it
has finished.

Letterbox crop detection is automatic and always on. How dark a pixel must be to count as black is
fixed. Neither is exposed as a setting.

## Refinement

All three stages are on by default. See [Boundary refinement](refinement.md).

| Setting | Default | Effect |
| --- | --- | --- |
| Silence Snapping | On | Move boundaries onto nearby silence |
| Silence Noise Floor | -50 dB | Level below which audio counts as silent (-90 to 0) |
| Minimum Silence Duration | 0.33 s | Shortest gap that counts (0.1 to 5) |
| Silence Snap Inward | 5.0 s | How far towards the middle of the segment to look (0.5 to 30) |
| Silence Snap Outward | 2.0 s | How far away from it to look (0.5 to 30) |
| Chapter Snapping | On | Move boundaries onto chapter marks |
| Chapter Snap Window | 5.0 s | Maximum distance to a usable mark (0.5 to 60) |
| Keyframe Snapping | On | Move boundaries onto video keyframes |
| Keyframe Snap Window | 3.0 s | Maximum distance to a usable keyframe (0.5 to 30) |

Changing any of these recomputes segments from cached detection data. Nothing is extracted again.

## Chromaprint tuning

Defaults are good. These exist for unusual libraries, and loosening them mostly adds false matches.

| Setting | Default | Effect | On change |
| --- | --- | --- | --- |
| Sample Rate | 22050 Hz | Audio is downmixed to mono at this rate before fingerprinting (8000 to 48000) | Re-runs, including extraction |
| Max Bit Errors | 6 | How different two fingerprint points may be and still match (0 to 32) | Recomputes |
| Max Time Skip | 3.5 s | Largest gap inside one matched run (0 to 30) | Recomputes |
| Inverted Index Fuzz | 2 | How far the index lookup reaches around each value (0 to 8) | Recomputes |

Changing the sample rate invalidates every stored fingerprint and re-extracts the whole library.

## Chapter names

One editable list per segment type. Entries are literal text, matched as whole words and
case-insensitively, not regular expressions. Changing a list re-runs chapter analysis.

## General

| Setting | Default | Effect |
| --- | --- | --- |
| Max Parallel Groups | 2 | How many seasons are analyzed at once (1 to 16, capped at the CPU count) |
| Force Regenerate | Off | One-shot: push every cached segment to Jellyfin again, overwriting |

**Force Regenerate** re-pushes rather than re-analyzes. Use it when Jellyfin's copy of the segments
has drifted from the plugin's. It clears itself after the run.

Per-library control is Jellyfin's own, under **Disabled media segment providers** in the library
settings.
