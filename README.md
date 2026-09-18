<h1 align="center">Jellyfin Segment Recognition Plugin</h1>

<p align="center">
<img alt="Plugin Banner" src="https://raw.githubusercontent.com/Shadowghost/jellyfin-plugin-segment-recognition/main/images/jellyfin-plugin-segment-recognition.svg?sanitize=true"/>
<br/>
<br/>
<a href="https://github.com/Shadowghost/jellyfin-plugin-segment-recognition/actions/workflows/build.yaml">
<img alt="Build status" src="https://img.shields.io/github/actions/workflow/status/Shadowghost/jellyfin-plugin-segment-recognition/build.yaml?branch=main&label=build"/>
</a>
<a href="https://github.com/Shadowghost/jellyfin-plugin-segment-recognition/actions/workflows/test.yaml">
<img alt="Test status" src="https://img.shields.io/github/actions/workflow/status/Shadowghost/jellyfin-plugin-segment-recognition/test.yaml?branch=main&label=tests"/>
</a>
<a href="https://github.com/Shadowghost/jellyfin-plugin-segment-recognition/releases">
<img alt="Current release" src="https://img.shields.io/github/v/release/Shadowghost/jellyfin-plugin-segment-recognition"/>
</a>
<a href="https://github.com/Shadowghost/jellyfin-plugin-segment-recognition/blob/main/LICENSE">
<img alt="GPL-3.0 license" src="https://img.shields.io/github/license/Shadowghost/jellyfin-plugin-segment-recognition"/>
</a>
</p>

Automatically finds intros, outros, recaps, previews and commercials in your library and hands them
to Jellyfin, so clients can show a skip button.

## Detection methods

Each can be turned on or off independently.

| Method | Default | What it does |
| --- | --- | --- |
| **Chapter names** | On | Matches chapter titles against configurable name lists (multi-language). Fast, no decoding. |
| **Audio fingerprinting** | On | Compares episodes within a season to find the opening and credits they share. TV libraries only. |
| **EDL import** | On | Reads `.edl` sidecar files next to your media. |
| **Black frames** | Off | Finds the black gaps that frame an intro or credits, using ffmpeg with optional hardware decoding and automatic letterbox detection. Decodes video, so it is the slowest method. |

Within audio fingerprinting, credits detection and pruning of misplaced segments are on by default,
preview detection is off. All boundary refinement (silence, chapter and keyframe snapping) is on.

Detected boundaries are snapped to nearby silence, chapter marks or keyframes so skips land cleanly.
Segments can also be exported back out as `.edl` sidecars.

[docs/](docs/) describes each method in detail, along with every setting and what changing it costs.

## How it works

Analysis and playback are kept apart:

- A scheduled task, **Analyze Segments** (daily at 02:00 by default), does all the work and stores
  its results. Run it manually from **Dashboard > Scheduled Tasks** if you don't want to wait.
- When a client asks for segments, the plugin only reads what that task already stored. No
  analysis happens during playback.
- Only new items are analyzed, so later runs are quick. Changing a setting marks the affected items
  for re-analysis automatically.

Fingerprinting adapts per season: if a season looks like it has openings the first scan couldn't
reach, those episodes are scanned again over a wider window. Segments that land where almost no
other episode in the season has one are discarded.

## Installation

Add one of these repository URLs in **Dashboard > Plugins > Repositories**:

```text
Stable:   https://raw.githubusercontent.com/Shadowghost/jellyfin-plugin-segment-recognition/metadata/stable/manifest.json
Unstable: https://raw.githubusercontent.com/Shadowghost/jellyfin-plugin-segment-recognition/metadata/unstable/manifest.json
```

Then install **Segment Recognition** from the catalog and restart Jellyfin.

### Building from source

```bash
dotnet build jellyfin-plugin-segment-recognition.slnx -c Release
```

Copy the resulting `Jellyfin.Plugin.SegmentRecognition.dll` into
`<jellyfin-data>/plugins/Segment Recognition_1.0.0.0/` alongside a `meta.json`, then restart Jellyfin.

## Configuration

**Dashboard > Plugins > Segment Recognition**, or the *Segment Recognition* entry in the main menu.
The defaults are sensible; the settings worth knowing are:

- **Providers**: which detection methods run, with the defaults listed above.
- **Duration limits**: how long a segment of each type is allowed to be, so unrelated matches get
  rejected.
- **Black frame**: how dark a frame counts as black, and minimum length of a black run. Because
  these settings decide what gets stored, use **Re-analyze Black Frames** to apply a change to
  already-scanned items.
- **Refinement**: silence, chapter and keyframe snapping, all on by default.
- **Chapter names**: the name lists used for chapter matching.

Per-library control uses Jellyfin's own **Disabled media segment providers** setting in the library
configuration.

## Data and troubleshooting

Results live in `<jellyfin-data>/data/segment-recognition/segments.db`. Deleting that file forces a
full re-analysis on the next task run.

Audio fingerprinting needs an ffmpeg build with chromaprint support. The plugin checks for it at
startup and logs a single warning if it's missing, then skips fingerprinting; the other methods keep
working.

To compare hardware acceleration options on your own hardware:

```bash
./tools/time-ffmpeg.sh "/path/to/file.mkv"            # software
./tools/time-ffmpeg.sh --hw cuda "/path/to/file.mkv"  # also: vaapi, qsv
```

## Requirements

- Jellyfin 12
- ffmpeg (shipped with Jellyfin), with chromaprint support for audio fingerprinting
- Optional: VAAPI, CUDA, QSV or VideoToolbox for faster black frame detection

## Acknowledgments

Parts of this plugin are inspired by [intro-skipper](https://github.com/intro-skipper/intro-skipper):

- The fingerprint comparison: an inverted index proposes how two episodes line up, then a
  point-by-point scan verifies the match.
- Measuring "how black is black" against the darkness a scan actually contains, so dark material
  does not report every night scene as a transition.
