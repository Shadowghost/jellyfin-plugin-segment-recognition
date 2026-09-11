# Chapter names

**On by default. Applies to any video. No decoding, so it costs almost nothing.**

Many releases already mark their own openings and endings as chapters. If the file says
"Opening Credits" at 90 seconds, that is better evidence than anything detection can infer, and it
is free to read.

## How it works

For each chapter whose title matches one of the configured name lists, the chapter becomes a
segment of that type.

**1. Match the title.** Every configured name is matched as a whole word, case-insensitively. The
word boundary is what keeps "Intro" from matching "Introvert", while still matching titles like
`(Intro)`, `Recap/Intro` and `Intro:` where the keyword is wrapped in punctuation.

Titles ending the segment rather than being it are rejected: "Intro End" and "Credits: End" mark a
boundary, not an opening, so a keyword directly followed by "End" does not match. Words merely
starting with those letters, such as "Ending" or "Endgame", are unaffected.

**2. Resolve competing matches.** A title can hit more than one list. "Opening Credits" contains
both the intro keyword "Opening" and the outro keyword "Credits". The longest matched keyword wins,
because it is the more specific one, and a tie is broken by whichever appears first in the title.

**3. Find the end.** A chapter runs until the next chapter starts. The last chapter runs to the end
of the item. Without that rule the single most common outro layout, a trailing "Credits" chapter,
would have no end position and be dropped.

**4. Check the length.** The segment is discarded unless its duration falls inside the window for
its type. See [Configuration](../configuration.md). A "Recap" chapter lasting the whole episode is
a mislabelled chapter, not a recap.

**5. Join what belongs together.** Chapters abut exactly, so a title sequence split across an
"Introduction" chapter and an "OP" chapter would otherwise produce two adjacent intro segments and
two skip buttons for one opening. Runs of the same type that touch or overlap are joined into one.

Only touching runs are joined. The same type genuinely recurs apart from itself, ad breaks being
the obvious case, and `Intro -> Commercial -> Intro` is a valid arrangement. A join that would push
the result outside the type's duration window is abandoned rather than manufacturing a segment the
length check would have rejected.

## Configuring the name lists

Each segment type has its own list, editable in the plugin settings. The defaults cover English,
German, French, Spanish, Japanese and Chinese, plus the abbreviations common on anime releases
(`OP`, `ED`, `PV`, `CM`).

Entries are plain text, not patterns. Regular expression characters are escaped, so an entry like
`Intro (Part 1)` matches that literal title. Adding a name that is a substring of common words is
the usual cause of false positives, though the word boundary rule makes that rarer than it sounds.

## Limits

Chapter titles longer than 512 characters are ignored, and each individual match attempt is capped
at one second. Both exist to keep a crafted or corrupt title from consuming the analysis run.

## When it finds nothing

Most files have no chapters at all, and many have chapters named only "Chapter 1", "Chapter 2".
Nothing here can help with those. Use [audio fingerprinting](audio-fingerprinting.md) for episodes
or [black frames](black-frames.md) for anything else.
