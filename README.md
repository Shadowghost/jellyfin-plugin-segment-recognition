# Jellyfin Segment Recognition Plugin

A Jellyfin plugin that automatically detects and manages media segments (intros, outros, recaps, previews) using multiple analysis strategies.

## Features

- **Chapter Name Matching** -- Identifies segments by matching chapter names against configurable patterns. Supports multiple languages.
- **Black Frame Detection** -- Detects black frame clusters at intro/outro boundaries using ffmpeg. Hardware-accelerated decoding with GPU-side downscaling (480p default). Automatic letterbox crop detection. Supports episodes and movies.
- **Chromaprint Audio Fingerprinting** -- Compares audio fingerprints across episodes in a season to find shared intro and credits sequences. Separate intro and credits region fingerprinting.
- **Preview Inference** -- Optionally detects preview/next-episode teasers after credits.
- **Boundary Refinement** -- Snaps detected boundaries to silence gaps (asymmetric windows), chapter markers, and video keyframes for clean skip transitions.
- **Config Staleness Detection** -- Results store a hash of the configuration that produced them. Stale results are automatically regenerated when settings change.
- **Incremental Processing** -- Only analyzes new items and pushes segments for items with new results.
- **EDL Export/Import** -- Exports segments as Kodi/MPlayer-compatible `.edl` sidecar files. Imports `.edl` files with support for standard 3-column and an extended format with segment type names for lossless round-trips.
- **Intro Skipper Import** -- One-time migration from the intro-skipper plugin database.

## How It Works

Analysis is decoupled from segment serving:

1. **Providers are cache-only.** When Jellyfin asks a provider for segments, it reads from the plugin's SQLite database. No ffmpeg, no analysis. Exception: EdlImportProvider parses `.edl` files on query.
2. **A scheduled task does the heavy lifting.** The `Analyze Segments` task (default: daily at 02:00) iterates all video items in a single pass — chapter name analysis, black frame analysis (with crop detection and hw accel), and chromaprint fingerprint generation.
3. **Group comparison pass.** Season groups with new fingerprints or stale results are compared pairwise. Segments pass through the refinement pipeline (silence -> chapter -> keyframe snapping), then are pushed to Jellyfin.
4. **Per-library provider control.** Respects Jellyfin's per-library `DisabledMediaSegmentProviders` setting. ChromaprintProvider only appears for TV show libraries; ChapterName, BlackFrame, and EdlImport appear for any video library.

## Installation

Add one of the following repository manifest URLs in **Dashboard > Plugins > Repositories**:

```
Stable:   https://raw.githubusercontent.com/Shadowghost/jellyfin-plugin-segment-recognition/metadata/stable/manifest.json
Unstable: https://raw.githubusercontent.com/Shadowghost/jellyfin-plugin-segment-recognition/metadata/unstable/manifest.json
```

Then install "Segment Recognition" from the plugin catalog and restart Jellyfin.

### Manual installation

```bash
dotnet build jellyfin-plugin-segment-recognition.slnx -c Release
```

Copy `Jellyfin.Plugin.SegmentRecognition/bin/Release/net10.0/Jellyfin.Plugin.SegmentRecognition.dll` to `<jellyfin-data>/plugins/Segment Recognition_1.0.0.0/` with a `meta.json`, and restart Jellyfin.

## Configuration

Access from **Dashboard > Plugins > Segment Recognition**.

**Providers** -- Toggle independently: Chapter Name, Black Frame, Chromaprint, EDL Import. Credits fingerprinting and preview inference are sub-options of Chromaprint.

**Duration Limits** -- Min/max intro (15-120s), min/max outro (15-600s), max movie outro (900s).

**Black Frame** -- Analysis resolution (480p/720p/native), re-analyze flag, automatic letterbox detection.

**Analysis Regions** -- Intro region (25% from start), outro region (240s from end).

**Refinement** -- Silence snapping (noise floor, min duration, asymmetric windows), chapter snapping (5.0s window), keyframe snapping (3.0s window). All enabled by default.

**Chromaprint** -- Max analysis duration (600s), min match duration (15s).

**Chapter Names** -- Configurable name lists per segment type with word-boundary matching.

## Data Storage

Analysis cache in `<jellyfin-data>/data/segment-recognition/segments.db` (SQLite):
- `AnalysisStatuses` -- Which items have been analyzed by which provider
- `ChapterAnalysisResults` -- Segments from chapter matching, chromaprint, and EDL import
- `BlackFrameResults` / `CropDetectResults` -- Raw black frame and crop data
- `ChromaprintResults` -- Audio fingerprints per item (intro and credits regions)

Deleting the database forces re-analysis on the next task run.

## Benchmarking

```bash
./tools/time-ffmpeg.sh "/path/to/file.mkv"                  # Software
./tools/time-ffmpeg.sh --hw cuda "/path/to/file.mkv"         # CUDA
./tools/time-ffmpeg.sh --hw vaapi "/path/to/file.mkv"        # VAAPI
./tools/time-ffmpeg.sh --hw qsv "/path/to/file.mkv"          # QSV
```

## Requirements

- Jellyfin 10.12+
- .NET 10
- ffmpeg (provided by Jellyfin) with chromaprint support for audio fingerprinting
- Optional: hardware acceleration support (VAAPI, CUDA, QSV, VideoToolbox) for faster black frame detection

## Acknowledgments

The audio fingerprint comparison algorithm is inspired by the approach used in [intro-skipper](https://github.com/intro-skipper/intro-skipper).
