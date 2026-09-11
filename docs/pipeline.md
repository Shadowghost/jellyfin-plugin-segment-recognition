# Pipeline

Analysis and playback are separate. A scheduled task does all the work and stores what it finds;
when a client asks for segments, the plugin only reads that store. No detection runs while
something is playing.

## The task

**Analyze Segments** appears under **Dashboard > Scheduled Tasks** in the *Segment Recognition*
category and runs daily at 02:00. Run it manually at any time. A second task, **Export EDL Files**,
has no schedule and only runs when started by hand.

A run goes through these stages.

### 1. One-shot flags

If **Re-analyze Black Frames** is set, every cached black frame, crop result, black-frame segment
and black-frame status row is deleted first, so the scan starts from nothing.

Both this flag and **Force Regenerate** are cleared at the *end* of the run, not the start. A run
that is cancelled halfway leaves the request in place so the next run still honours it.

### 2. Orphan sweep

Jellyfin derives an item's ID from its path and library options, so renaming a file or re-adding a
library mints a new ID without announcing that the old one is gone. The sweep deletes cached rows
whose item no longer exists.

It refuses to delete when the result looks untrustworthy: if more than half of the known items are
suddenly missing, and at least 20 items are known, the run is treated as an incomplete library read
and nothing is removed. A locked library database or an unmounted share would otherwise wipe the
cache and cost hours of re-analysis.

### 3. Per item

Episodes are grouped by season and movies handled individually, then each item runs the enabled
methods:

- [chapter names](methods/chapter-names.md)
- [black frames](methods/black-frames.md)
- fingerprint generation for [audio fingerprinting](methods/audio-fingerprinting.md), for episodes
  only

An item is skipped if it was already analyzed under the same configuration. See
[Storage](storage.md) for how that is decided.

Seasons are processed in parallel, up to **Max Parallel Groups** (default 2) and never more than
the CPU count.

### 4. Per season

Once every episode in a season has a fingerprint, the season is compared as a whole. This is where
intros and credits are actually found, because a shared opening only exists relative to other
episodes.

The comparison runs when new fingerprints were generated, or when the stored results no longer
match the current configuration.

Regardless of that, every season is checked for episodes whose opening the first scan may have
missed. Those episodes are fingerprinted again over a wider region and compared once more. This
check runs on every pass because a season with a cut-off intro otherwise looks perfectly up to
date. It is cheap when there is nothing to do, and an episode is only ever retried once.

### 5. Push

Analyzed items are handed to Jellyfin's segment store.

The push covers every item that has *ever* been analyzed, not only the ones that currently have
results. Jellyfin removes a segment when the provider is asked again and returns nothing, so an
item that just lost its results is precisely the one that has to be pushed. Items that were never
analyzed are skipped, since Jellyfin was never given anything for them.

## Serving

Each detection method registers as a Jellyfin media segment provider:

| Provider | Applies to |
| --- | --- |
| `ChapterName` | any video |
| `BlackFrame` | any video |
| `Chromaprint` | episodes only |
| `EdlImport` | any video |

They read from the cache and return immediately. The one exception is EDL import, which parses the
sidecar file when asked.

Providers can be switched off per library through Jellyfin's own **Disabled media segment
providers** list in the library settings, which the task honours as well: a provider disabled for a
library is not run for its items.
