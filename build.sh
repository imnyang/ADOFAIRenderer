#!/usr/bin/env bash
set -euo pipefail

# Cross-platform build entry point. The game DLLs must come from the local
# ADOFAI installation; no game binaries or FFmpeg binaries are redistributed.
ROOT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
GAME_DIR="${GAME_DIR:-}"
MSBUILD_PATH="${MSBUILD_PATH:-}"

if [[ -z "$GAME_DIR" ]]; then
  echo "Set GAME_DIR to the ADOFAI installation directory." >&2
  echo "Example: GAME_DIR=\"$HOME/.steam/steam/steamapps/common/A Dance of Fire and Ice\" bash ./build.sh" >&2
  exit 2
fi

MANAGED_DIR="$GAME_DIR/A Dance of Fire and Ice_Data/Managed"
if [[ ! -f "$MANAGED_DIR/Assembly-CSharp.dll" ]]; then
  echo "ADOFAI managed assemblies were not found: $MANAGED_DIR" >&2
  exit 2
fi

if [[ -n "$MSBUILD_PATH" ]]; then
  if ! command -v "$MSBUILD_PATH" >/dev/null 2>&1 && [[ ! -x "$MSBUILD_PATH" ]]; then
    echo "The configured MSBUILD_PATH was not found: $MSBUILD_PATH" >&2
    exit 2
  fi
  case "$(basename "$MSBUILD_PATH")" in
    dotnet|dotnet.exe) MSBUILD_COMMAND=("$MSBUILD_PATH" msbuild) ;;
    *) MSBUILD_COMMAND=("$MSBUILD_PATH") ;;
  esac
elif command -v msbuild >/dev/null 2>&1; then
  MSBUILD_COMMAND=(msbuild)
elif command -v xbuild >/dev/null 2>&1; then
  MSBUILD_COMMAND=(xbuild)
elif command -v dotnet >/dev/null 2>&1; then
  MSBUILD_COMMAND=(dotnet msbuild)
else
  echo "No MSBuild toolchain was found. Install msbuild, xbuild, or the .NET SDK, or set MSBUILD_PATH." >&2
  exit 2
fi

cd "$ROOT_DIR"
"${MSBUILD_COMMAND[@]}" ADOFAIRenderer.sln \
  /t:Rebuild \
  /p:Configuration=Release \
  "/p:GameDir=$GAME_DIR" \
  /v:minimal

# Remove FFmpeg artifacts produced by older versions of this build script.
# The exact Release/FFmpeg directory is generated output, not user data.
RELEASE_ROOT="$ROOT_DIR/ADOFAIRenderer/bin/Release"
if [[ -d "$RELEASE_ROOT/FFmpeg" ]]; then
  rm -rf "$RELEASE_ROOT/FFmpeg"
fi
for stale_name in ffmpeg ffmpeg.exe ffprobe ffprobe.exe FFmpeg-LICENSE.txt FFmpeg-README.txt; do
  if [[ -f "$RELEASE_ROOT/$stale_name" ]]; then
    rm -f "$RELEASE_ROOT/$stale_name"
  fi
done

echo "Mod output: $ROOT_DIR/ADOFAIRenderer/bin/Release"
echo "FFmpeg will be downloaded by the mod on first launch."
echo "Install the output folder under the game's Mods directory."
