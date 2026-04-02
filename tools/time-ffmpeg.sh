#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: $0 [--hw cuda|vaapi|qsv|videotoolbox] <media-file>"
    echo ""
    echo "Options:"
    echo "  --hw cuda           Use NVIDIA CUDA/NVDEC hardware decoding"
    echo "  --hw vaapi          Use VAAPI hardware decoding (default device: /dev/dri/renderD128)"
    echo "  --hw qsv            Use Intel Quick Sync Video hardware decoding"
    echo "  --hw videotoolbox   Use Apple VideoToolbox hardware decoding"
    exit 1
}

if [ $# -lt 1 ]; then
    usage
fi

FILE=""
HW_TYPE=""

while [ $# -gt 0 ]; do
    case "$1" in
        --hw)
            shift
            HW_TYPE="${1:-}"
            if [ -z "$HW_TYPE" ]; then
                echo "Error: --hw requires an argument"
                usage
            fi
            shift
            ;;
        -*)
            echo "Error: unknown option $1"
            usage
            ;;
        *)
            FILE="$1"
            shift
            ;;
    esac
done

if [ -z "$FILE" ]; then
    echo "Error: no media file specified"
    usage
fi

if [ ! -f "$FILE" ]; then
    echo "Error: file not found: $FILE"
    exit 1
fi

# Build hwaccel input args as an array and filter prefixes.
# Uses proper -init_hw_device initialization matching Jellyfin's transcoding pipeline.
# HW_FILTER_PREFIX is for cropdetect (full resolution).
# HW_SCALE_PREFIX is for blackframe (GPU-scaled to 480p for speed).
HW_INPUT=()
HW_FILTER_PREFIX=""
HW_SCALE_PREFIX=""

case "$HW_TYPE" in
    cuda)
        HW_INPUT=(-init_hw_device cuda=cu:0 -filter_hw_device cu -hwaccel cuda -hwaccel_output_format cuda)
        HW_FILTER_PREFIX="hwdownload,format=nv12,"
        HW_SCALE_PREFIX="scale_cuda=-2:480:format=nv12,hwdownload,format=nv12,"
        ;;
    vaapi)
        VAAPI_DEVICE="${VAAPI_DEVICE:-/dev/dri/renderD128}"
        HW_INPUT=(-init_hw_device "vaapi=va:${VAAPI_DEVICE}" -filter_hw_device va -hwaccel vaapi -hwaccel_output_format vaapi)
        HW_FILTER_PREFIX="hwdownload,format=nv12,"
        HW_SCALE_PREFIX="scale_vaapi=h=480:w=-2:format=nv12,hwdownload,format=nv12,"
        ;;
    qsv)
        VAAPI_DEVICE="${VAAPI_DEVICE:-/dev/dri/renderD128}"
        HW_INPUT=(-init_hw_device "vaapi=va:${VAAPI_DEVICE}" -init_hw_device qsv=qs@va -filter_hw_device qs -hwaccel qsv -hwaccel_output_format qsv)
        HW_FILTER_PREFIX="vpp_qsv=format=nv12,hwdownload,format=nv12,"
        HW_SCALE_PREFIX="vpp_qsv=h=480:w=-1:format=nv12,hwdownload,format=nv12,"
        ;;
    videotoolbox)
        HW_INPUT=(-init_hw_device videotoolbox=vt -hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld)
        HW_FILTER_PREFIX="hwdownload,format=nv12,"
        HW_SCALE_PREFIX="scale_vt=h=480:w=-2,hwdownload,format=nv12,"
        ;;
    "")
        HW_SCALE_PREFIX="scale=-2:480,"
        ;;
    *)
        echo "Error: unknown hw type '$HW_TYPE'"
        usage
        ;;
esac

DURATION=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$FILE")
CODEC=$(ffprobe -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 "$FILE")

echo "File: $FILE"
echo "Duration: ${DURATION}s"
echo "Video codec: ${CODEC}"
if [ -n "$HW_TYPE" ]; then
    echo "Hardware accel: ${HW_TYPE}"
    echo "  Input args: ${HW_INPUT[*]}"
    echo "  Filter prefix: ${HW_FILTER_PREFIX:-none}"
else
    echo "Hardware accel: none (software decoding)"
fi
echo ""

# Validate hw accel works with a quick 1-frame test
if [ ${#HW_INPUT[@]} -gt 0 ]; then
    echo "Validating hardware acceleration..."
    if ! ffmpeg "${HW_INPUT[@]}" -t 0.1 -i "$FILE" -vf "${HW_FILTER_PREFIX}null" -an -sn -dn -f null - 2>/dev/null; then
        echo "ERROR: Hardware acceleration failed. stderr:"
        ffmpeg "${HW_INPUT[@]}" -t 0.1 -i "$FILE" -vf "${HW_FILTER_PREFIX}null" -an -sn -dn -f null - 2>&1 | grep -i -E "error|cannot|could not|failed|not found" || true
        exit 1
    fi
    echo "Hardware acceleration OK"
    echo ""
fi

INTRO_SCAN=$(awk "BEGIN { v=$DURATION*0.25; print (v<240?v:240) }")
OUTRO_START=$(awk "BEGIN { v=$DURATION-240; print (v>0?v:0) }")
OUTRO_SCAN=$(awk "BEGIN { print $DURATION-${OUTRO_START} }")
CROP_START=$(awk "BEGIN { print $DURATION*0.4 }")
CHROMA_SCAN=$(awk "BEGIN { v=$DURATION*0.25; print (v<600?v:600) }")

run_timed() {
    local label="$1"
    shift
    echo "=== ${label} ==="
    if ! time "$@" 2>/dev/null; then
        echo "  FAILED (exit code $?), re-running with stderr:"
        "$@" 2>&1 | grep -i -E "error|cannot|could not|failed|not found" | head -5 || true
    fi
    echo ""
}

run_timed "Cropdetect (10s sample from 40%)" \
    ffmpeg "${HW_INPUT[@]}" -ss "$CROP_START" -t 10 -i "$FILE" -vf "${HW_FILTER_PREFIX}cropdetect=round=2" -an -sn -dn -f null -

run_timed "Black frame - intro region (${INTRO_SCAN}s)" \
    ffmpeg "${HW_INPUT[@]}" -ss 0 -t "$INTRO_SCAN" -i "$FILE" -vf "${HW_SCALE_PREFIX}blackframe=threshold=90" -an -sn -dn -f null -

run_timed "Black frame - outro region (${OUTRO_SCAN}s from ${OUTRO_START}s)" \
    ffmpeg "${HW_INPUT[@]}" -ss "$OUTRO_START" -t "$OUTRO_SCAN" -i "$FILE" -vf "${HW_SCALE_PREFIX}blackframe=threshold=90" -an -sn -dn -f null -

echo "=== Chromaprint (${CHROMA_SCAN}s) ==="
TMPFILE=$(mktemp)
time ffmpeg -y -ss 0 -i "$FILE" -ac 1 -ar 22050 -t "$CHROMA_SCAN" -vn -sn -dn -f chromaprint -fp_format raw "$TMPFILE" 2>/dev/null
echo "Fingerprint size: $(wc -c < "$TMPFILE") bytes"
rm -f "$TMPFILE"
