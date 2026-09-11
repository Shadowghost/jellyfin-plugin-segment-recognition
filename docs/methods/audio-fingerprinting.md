# Audio fingerprinting

**On by default. Episodes only.**

A title sequence is the same audio in every episode of a season. Nothing about one episode on its
own says where its opening is, but the audio that recurs across siblings does, and that is what
this method looks for. The same applies to closing credits.

Because it compares episodes with each other, it needs a season with more than one episode. Movies
are never fingerprinted.

## 1. Which part of the file is fingerprinted

Fingerprinting the whole file would be wasteful, so two regions are taken.

| Region | Covers |
| --- | --- |
| Intro | the first 25% of the runtime, at most 10 minutes |
| Credits | the last 4 minutes, measured from the end of the audio |

Items of 10 minutes or less are fingerprinted whole, since they have no room for two separate
regions.

The intro region is not a setting. It decides only how much is searched on the *first* pass, and
anything it misses is picked up by the retry described below. 99% of matched intros end within 22%
of the runtime, so the first pass is right nearly always and the wider search stays rare.

The credits region is anchored to the audio, not to the container runtime. Some files carry a
duration longer than their actual audio, and measuring from the wrong end puts the region in the
wrong place.

## 2. Generating the fingerprint

ffmpeg produces a raw chromaprint fingerprint of the region, downmixed to mono at the configured
sample rate (22050 Hz). Each fingerprint is a sequence of 32-bit values, one roughly every 0.124
seconds, each describing the sound around that moment.

Fingerprints have to come from this exact invocation. Points produced by a different pipeline
compare against locally generated ones with a systematically higher error rate, so they cannot be
mixed.

Each stored fingerprint records the span it covers and where that span starts, so a later pass can
tell whether it is already wide enough.

## 3. Comparing two episodes

Given two fingerprints, the question is whether a run of one appears in the other, and where.

**Propose alignments.** An index maps every value in the second fingerprint to the positions it
occurs at. Each value of the first is looked up in it, including neighbouring values to absorb
small encoding differences, and each hit votes for one alignment: "this episode's audio sits N
points ahead of that one's". Real shared audio produces many votes for the same alignment.

A value repeating in a long constant stretch, such as digital silence, is capped so it cannot swamp
the tally.

**Verify them.** An alignment with at least two votes is checked properly: both fingerprints are
walked in step and each pair of points compared bit by bit. Points differing by at most **Max Bit
Errors** (default 6) count as matching. A run of matching points ends when the gap to the next match
exceeds **Max Time Skip** (default 3.5 s), and is kept if it lasts at least the minimum duration for
the segment type.

The vote threshold is deliberately low. Votes require near-exact point matches, so a genuinely
matching intro that is noisy, perhaps a surround to mono downmix, may collect only a couple of votes
at the correct alignment. The bit-by-bit verification is the real filter; spurious alignments simply
produce no qualifying run.

Every qualifying run is returned, longest first, rather than only the single best. A shared opening
plus the content after it can form one long run that overshoots the duration window, while the
correct region sits just below it in the list.

## 4. Choosing an episode's segment

Each episode is compared against up to 8 counterparts, taken as its nearest neighbours in episode
order, alternating earlier and later.

Nearest neighbours rather than the first episodes of the season, because a season that changes its
opening partway through, such as a split-cour anime or a mid-season rebrand, shares only the brief
distributor ident between its two halves. Comparing everything against the head of the season
collapsed the second half onto that ident. Walking outwards keeps comparisons inside the run of
episodes likely to share an opening, for the same number of comparisons.

Different versions of the same title are never compared with each other, since two copies of one
episode share all of their audio.

Each counterpart contributes **one candidate**: the longest region it produced that fits the
window. An intro has to start in the first half of the file and last between the minimum and maximum
intro duration; credits have to start after the midpoint and fit the outro window.

**The vote.** Candidates are grouped by start position, within 2 seconds of each other. The largest
group wins, and the segment is its median start and end, so one stray candidate inside an otherwise
agreeing group cannot stretch the boundary.

A region needs **two** counterparts to agree before it is accepted. One agreeing counterpart is
enough only when that is all there is, as in a two-episode season. This is what stops a single
spurious pairing from defining an episode's intro.

When two groups have equal support, the longer region wins. At a mid-season opening change, an
episode on the boundary splits its counterparts evenly between the old opening and the new one, and
the two groups are not equally informative: one is a real opening of a minute or more, the other is
whatever short ident every file begins with. Preferring the earlier one would hand the segment to
the ident.

## 5. Widening the search

Some openings sit outside the first-pass region. A long-form drama can run fifteen minutes of cold
open before its titles.

After each comparison, a season is checked for two shapes: episodes with no intro at all, and
episodes whose intro is less than half the length of the season's longest. The second matters as
much as the first, because an episode whose opening lies beyond the region often still matches a
brief shared ident at the head of the file, which looks like success.

Those episodes are fingerprinted again over the first half of the file, which is everything the
window check would still accept, and compared once more.

Episodes already fingerprinted at that width are not offered again, so a season that genuinely has
no shared opening settles after one retry instead of re-extracting every night.

## 6. Discarding misplaced segments

*Controlled by **Season Outlier Pruning**, on by default.*

An episode with no opening of its own can still match an incidental music cue it shares with a
sibling, producing a segment at an arbitrary position that looks perfectly valid in isolation.

Across a season, positions tell the difference. Every segment of one kind is collected, measuring
intros from the start of the file and outros from the end, and the positions are grouped by
proximity. Groups too small to be a format variant, fewer than 3 episodes, are discarded.

Two rules keep this from removing good results:

- **Only when one position dominates.** Unless a single group holds at least 60% of the season,
  nothing is pruned. Without a dominant position there is nothing to be a straggler from, and
  guessing would discard as many good segments as bad. Seasons carry several legitimate positions:
  one 197-episode arc splits 141/41/15 across three, all correct.
- **Groups chain on their previous member,** not on the first. Where the cold open varies
  continuously the positions form a run rather than tight clusters. One season spreads its intros
  from 22 s to 292 s with no consecutive gap above 93 s. Anchoring on the first member would chop
  that run into pieces and prune its tail.

Seasons with fewer than 6 segments are left alone entirely: too little evidence to judge any of
them.

## 7. Previews after the credits

*Controlled by **Preview Inference**, off by default.*

When credits end before the file does, the tail is either a next-episode teaser or just a few
seconds of black before EOF. A tail shorter than the minimum preview duration is absorbed into the
outro rather than surfaced as a misleading one-second preview. A longer tail, up to the maximum
preview duration, becomes a Preview segment. Anything longer is left alone, so a post-credits scene
is not mistaken for a teaser.

Preview rows are derived from the credits result and are deleted alongside it whenever the credits
are recomputed.

## Why an episode has no result

Every attempt records its outcome, so "this episode has no intro" can be told apart from "this
episode was never analyzed". See [Troubleshooting](../troubleshooting.md) for the list.
