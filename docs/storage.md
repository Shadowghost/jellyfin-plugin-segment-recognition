# Storage

Everything the analysis task produces is cached in one SQLite database:

```text
<jellyfin-data>/data/segment-recognition/segments.db
```

Deleting it forces a full re-analysis on the next task run. Nothing else is lost: the segments
Jellyfin currently serves are its own copy, and they are replaced as items are analyzed again.

## What is stored

| Table | Holds |
| --- | --- |
| `ChapterAnalysisResults` | The segments themselves, from every method, tagged by which one produced them |
| `AnalysisStatuses` | One row per item and method: when it ran, whether it found anything, and under which configuration |
| `ChromaprintResults` | Audio fingerprints, one per item and region, with the span each covers |
| `BlackFrameResults` | The frames that qualified as black |
| `CropDetectResults` | The detected picture area per item |

Fingerprints dominate the size. Black frame samples are next, and only exist if black frame
detection is enabled.

## How repeat work is avoided

Analysis is incremental: an item already analyzed under the current settings is skipped. Two
mechanisms decide that.

**A status row per item and method.** It records whether the run found anything. This matters most
for items that matched *nothing*: they have no segments to inspect, so without a status row there
would be no way to tell "analyzed, found nothing" from "never analyzed", and they would be
reprocessed forever.

**A configuration hash.** Each status row carries a fingerprint of the settings that were in effect.
When the settings change, the hash no longer matches and those items are analyzed again.

The hash is split by pipeline stage rather than being one value for the whole configuration. That
split is what lets a cheap change replay from cached data while only changes affecting extraction
pay for ffmpeg again. There are six stages: chapter names, black frame extraction, black frame
segments, intro fingerprints, credits fingerprints, and fingerprint comparison.

Black frame extraction is the deliberate exception: its hash never changes, so no settings change
can trigger a mass re-scan. [Re-analyze Black Frames](methods/black-frames.md#re-analyzing) is the
way to ask for one.

## When items disappear

Jellyfin derives an item's ID from its path and library options. Renaming a file, re-adding a
library, changing a library path, or a series matching to a different provider ID all mint a new ID
without reporting the old one as removed. The cached rows under the old ID are then unreachable.

Each task run sweeps them away. The sweep is cautious: if more than half the known items are
missing at once, and at least 20 are known, it assumes it read the library incorrectly and deletes
nothing. A locked library database or a temporarily unmounted share looks exactly like a mass
deletion, and the mistake is not recoverable, while leaving stale rows in place is.

## Concurrency

The database runs in WAL mode with a private cache, which lets the analysis task write from several
workers while providers read on the playback path.

The private cache is not incidental. SQLite's shared-cache mode replaces WAL's overlapping reads
and writes with process-wide table-level locks, so a provider read arriving during a write would
block for the full connection timeout and then fail with `SQLITE_LOCKED`, which, unlike
`SQLITE_BUSY`, is not retried.

## Schema changes

Migrations are applied automatically at startup. An upgrade needs no manual step.
