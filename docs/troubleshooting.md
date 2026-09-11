# Troubleshooting

## Nothing has been detected at all

Segments only appear after the **Analyze Segments** task has run. It runs daily at 02:00; on a
fresh install, start it by hand from **Dashboard > Scheduled Tasks**.

The first run is slow, because every item is new. Later runs only touch what changed.

Check next that the method you expect is enabled. Only chapter names, audio fingerprinting and EDL
import are on by default; [black frame detection](methods/black-frames.md) is off. Also check
Jellyfin's per-library **Disabled media segment providers** list, which the plugin honours.

## One episode has no intro while its siblings do

This is usually a real answer rather than a fault. Every fingerprint comparison records why it did
or did not produce a segment:

| Outcome | Meaning |
| --- | --- |
| `Matched` | A region was agreed on and stored |
| `NoComparableCounterparts` | Nothing to compare against. A single-episode season, or every other file is another version of the same episode |
| `NoSharedAudio` | Counterparts were compared, but none shared a long enough run of audio. The item's audio genuinely differs, typically a re-encode or a differently cut release in the same folder |
| `OutsideWindow` | Shared audio was found, but every match fell outside the duration or position limits for that segment type |
| `NoConsensus` | Matches were found, but too few counterparts agreed on one position |
| `SeasonOutlier` | A match was agreed on, then discarded for sitting where almost no other episode has one |

`NoSharedAudio` on a single episode of an otherwise healthy season nearly always means that file
differs from its siblings. `OutsideWindow` points at the duration limits in
[Configuration](configuration.md).

## Audio fingerprinting does nothing anywhere

It needs an ffmpeg build with chromaprint support. The plugin probes for it at startup and logs one
warning if it is missing, then skips the fingerprint pipeline entirely while the other methods keep
working.

Look for that warning in the server log. The probe checks for the chromaprint muxer, its raw
fingerprint output format, and the `silencedetect` and `blackframe` filters.

Fingerprinting also applies to episodes only, and needs more than one episode in the season.

## Detected segments are in the wrong place

Widen nothing first. Check the duration limits: a too-generous maximum is the most common cause of
a segment that starts in the right place and runs far too long.

For fingerprint results, [season outlier pruning][pruning] already discards positions the rest of
the season does not share, but it stands down when a season has no dominant position, which is
correct for shows whose placement genuinely varies.

[pruning]: methods/audio-fingerprinting.md#6-discarding-misplaced-segments

For black frame results, a low **Black Pixels Per Frame** lets ordinary dark scenes register as
transitions. Raise it rather than lowering it, and remember it applies to newly analyzed items until
**Re-analyze Black Frames** is used.

## Jellyfin still shows a segment the plugin no longer has

Jellyfin keeps its own copy and drops a segment only when the provider is asked again and returns
nothing. The analysis task pushes every item it has ever analyzed for exactly this reason, so a
normal run should clear it.

If it persists, **Force Regenerate** re-pushes everything, overwriting Jellyfin's copy. It clears
itself once the run finishes.

## Starting over

Delete `<jellyfin-data>/data/segment-recognition/segments.db` and run the task again. Nothing else
has to be cleaned up. See [Storage](storage.md).

## Getting more detail

Set the plugin's log level to Debug in Jellyfin's logging configuration. Each method logs what it
examined and why it rejected what it rejected, per item.
