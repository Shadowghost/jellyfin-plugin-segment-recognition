#!/usr/bin/env bash
# Local build + deploy for development against local jellyfin directory.
#
# Steps:
#   1. Pack Jellyfin source projects into /tmp/jellyfin-local-nuget. Each
#      project keeps its own <VersionPrefix> so the resulting AssemblyVersion
#      matches what the installed Jellyfin host expects (e.g. Keyframes
#      10.11.0.0 vs. Controller 12.0.0.0). The pack version gets a `-local`
#      suffix so it can't clash with the official feed.
#   2. Restore + build this plugin against that local feed via nuget.local.config.
#   3. Deploy the built DLL to the local Jellyfin plugins dir as Segment Recognition_local.
#
# Restart Jellyfin manually afterwards (this script does NOT touch the running
# server). When running from source that means stopping and re-running the host,
# e.g. dotnet run --project Jellyfin.Server ...

set -euo pipefail

JELLYFIN_SRC="${JELLYFIN_SRC:-$HOME/Documents/GitHub/jellyfin}"
PLUGIN_DIR="${PLUGIN_DIR:-$HOME/Documents/GitHub/jellyfin-plugin-segment-recognition}"
LOCAL_FEED="${LOCAL_FEED:-/tmp/jellyfin-local-nuget}"
LOCAL_SUFFIX="${LOCAL_SUFFIX:-local}"
DEPLOY_ROOT="${DEPLOY_ROOT:-/Volumes/Data/jellyfin/plugins}"
DEPLOY_NAME="${DEPLOY_NAME:-Segment Recognition_local}"
LOGO_NAME="jellyfin-plugin-segment-recognition.png"
REPO_LOGO="${REPO_LOGO:-$PLUGIN_DIR/images/$LOGO_NAME}"

JELLYFIN_PROJECTS=(
  "$JELLYFIN_SRC/MediaBrowser.Common/MediaBrowser.Common.csproj"
  "$JELLYFIN_SRC/MediaBrowser.Model/MediaBrowser.Model.csproj"
  "$JELLYFIN_SRC/MediaBrowser.Controller/MediaBrowser.Controller.csproj"
  "$JELLYFIN_SRC/Emby.Naming/Emby.Naming.csproj"
  "$JELLYFIN_SRC/Jellyfin.Data/Jellyfin.Data.csproj"
  "$JELLYFIN_SRC/src/Jellyfin.Extensions/Jellyfin.Extensions.csproj"
  "$JELLYFIN_SRC/src/Jellyfin.MediaEncoding.Keyframes/Jellyfin.MediaEncoding.Keyframes.csproj"
  "$JELLYFIN_SRC/src/Jellyfin.Database/Jellyfin.Database.Implementations/Jellyfin.Database.Implementations.csproj"
)

echo "[1/4] Packing ${#JELLYFIN_PROJECTS[@]} Jellyfin projects to $LOCAL_FEED (suffix=$LOCAL_SUFFIX)"
# Wipe stale packs so an old override-stamped 10.12.0-local doesn't shadow a
# fresh, correctly-versioned rebuild.
rm -rf "$LOCAL_FEED"
mkdir -p "$LOCAL_FEED"
for proj in "${JELLYFIN_PROJECTS[@]}"; do
  echo "  - $(basename "$proj")"
  dotnet pack "$proj" \
    -c Release \
    --version-suffix "$LOCAL_SUFFIX" \
    -o "$LOCAL_FEED" \
    --nologo \
    --verbosity quiet
done

echo "[2/4] Restoring plugin against local feed"
cd "$PLUGIN_DIR"
dotnet restore --configfile nuget.local.config --force --verbosity quiet

echo "[3/4] Building plugin (Release)"
dotnet build Jellyfin.Plugin.SegmentRecognition/Jellyfin.Plugin.SegmentRecognition.csproj \
  -c Release --no-restore --nologo --verbosity quiet

echo "[4/4] Deploying to $DEPLOY_ROOT/$DEPLOY_NAME"
DEST="$DEPLOY_ROOT/$DEPLOY_NAME"
mkdir -p "$DEST"
cp "$PLUGIN_DIR/Jellyfin.Plugin.SegmentRecognition/bin/Release/net10.0/Jellyfin.Plugin.SegmentRecognition.dll" "$DEST/"

# Copy the plugin logo shipped in this repo.
if [ -f "$REPO_LOGO" ]; then
  echo "  - logo: $REPO_LOGO"
  cp "$REPO_LOGO" "$DEST/$LOGO_NAME"
else
  echo "  - WARNING: no logo found at $REPO_LOGO; plugin will have no image"
fi

# Generate meta.json from scratch. A versioned plugin folder needs a valid meta.json with the
# correct GUID and the COMPLETE list of assemblies: when Assemblies is non-empty Jellyfin
# whitelists exactly those DLLs. The version is stamped high (9999...) so this dev build wins over
# any catalog-installed copy. Keep these values in sync with build.yaml.
python3 - <<EOF
import json, os, datetime
dest = "$DEST"
meta = {
    "category": "Metadata",
    "changelog": "",
    "description": "Recognizes and manages media segments using chapter name matching, black frame detection, chromaprint audio fingerprinting, and EDL import.",
    "guid": "b1c2d3e4-f5a6-7b8c-9d0e-1f2a3b4c5d6e",
    "name": "Segment Recognition",
    "overview": "Automatically detects and manages media segments (intros, outros, recaps, previews)",
    "owner": "Shadowghost",
    "targetAbi": "12.0.0.0",
    "framework": "net10.0",
    "version": "9999.0.0.0",
    "status": "Active",
    "autoUpdate": False,
    "timestamp": datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%dT%H:%M:%S.0000000Z"),
    "imagePath": os.path.join(dest, "$LOGO_NAME"),
    "assemblies": [
        "Jellyfin.Plugin.SegmentRecognition.dll",
    ],
}
with open(os.path.join(dest, "meta.json"), "w") as f:
    json.dump(meta, f, indent=2)
print("  - wrote meta.json (guid b1c2d3e4-..., targetAbi 12.0.0.0)")
EOF

echo
echo "Done. Restart Jellyfin manually to pick up the build (e.g. stop and re-run"
echo "the host: dotnet run --project Jellyfin.Server ...)."
