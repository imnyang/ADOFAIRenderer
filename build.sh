#!/usr/bin/env bash
set -euo pipefail

# Cross-platform build entry point. The game DLLs must come from the local
# ADOFAI installation; no game binaries are downloaded or redistributed.
ROOT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
GAME_DIR="${GAME_DIR:-}"
MSBUILD_PATH="${MSBUILD_PATH:-}"
FETCH_FFMPEG="${FETCH_FFMPEG:-1}"
FFMPEG_PLATFORM="${FFMPEG_PLATFORM:-}"
FFMPEG_PLATFORMS="${FFMPEG_PLATFORMS:-}"

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

if [[ -z "$FFMPEG_PLATFORMS" ]]; then
  # A regular build is always a complete distributable build. FFMPEG_PLATFORM
  # only selects the host-compatible root copy; it must not reduce the bundle.
  FFMPEG_PLATFORMS="windows-x64,linux-x64,macos-x64,macos-arm64"
fi

if [[ -z "$FFMPEG_PLATFORM" ]]; then
  case "$(uname -s)" in
    Darwin)
      case "$(uname -m)" in
        arm64|aarch64) FFMPEG_PLATFORM="macos-arm64" ;;
        *) FFMPEG_PLATFORM="macos-x64" ;;
      esac
      ;;
    Linux) FFMPEG_PLATFORM="linux-x64" ;;
    *) FFMPEG_PLATFORM="" ;;
  esac
fi

find_ffmpeg() {
  local platform="$1"
  local name="ffmpeg"
  [[ "$platform" == "windows-x64" ]] && name="ffmpeg.exe"
  local platform_dir="$ROOT_DIR/packages/ffmpeg/$platform"
  if [[ -f "$platform_dir/$name" ]]; then
    printf '%s\n' "$platform_dir/$name"
    return 0
  fi
  if [[ "$platform" == "windows-x64" && -d "$ROOT_DIR/packages/ffmpeg" ]]; then
    find "$ROOT_DIR/packages/ffmpeg" -type f -name "$name" -print -quit
  fi
}

download_file() {
  local url="$1"
  local destination="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fL --retry 3 --output "$destination" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget -O "$destination" "$url"
  else
    echo "curl or wget is required to download FFmpeg." >&2
    return 2
  fi
}

fetch_ffmpeg() {
  local platform="$1"
  local platform_dir="$ROOT_DIR/packages/ffmpeg/$platform"
  local download_dir="$ROOT_DIR/packages/ffmpeg/downloads"
  local archive extract source name probe metadata
  name="ffmpeg"
  [[ "$platform" == "windows-x64" ]] && name="ffmpeg.exe"
  mkdir -p "$platform_dir" "$download_dir"
  extract="$(mktemp -d)"

  case "$platform" in
    windows-x64)
      command -v unzip >/dev/null 2>&1 || { echo "unzip is required to extract Windows FFmpeg." >&2; rm -rf "$extract"; return 2; }
      archive="$download_dir/ffmpeg-windows-x64.zip"
      [[ -f "$archive" ]] || download_file "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" "$archive"
      unzip -q -o "$archive" -d "$extract"
      ;;
    linux-x64)
      command -v tar >/dev/null 2>&1 || { echo "tar is required to extract Linux FFmpeg." >&2; rm -rf "$extract"; return 2; }
      archive="$download_dir/ffmpeg-linux-x64.tar.xz"
      [[ -f "$archive" ]] || download_file "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz" "$archive"
      tar -xJf "$archive" -C "$extract"
      ;;
    macos-x64|macos-arm64)
      command -v unzip >/dev/null 2>&1 || { echo "unzip is required to extract macOS FFmpeg." >&2; rm -rf "$extract"; return 2; }
      if [[ "$platform" == "macos-arm64" ]]; then
        archive="$download_dir/ffmpeg-macos-arm64.zip"
        [[ -f "$archive" ]] || download_file "https://ffmpeg.martin-riedl.de/redirect/latest/macos/arm64/release/ffmpeg.zip" "$archive"
      else
        archive="$download_dir/ffmpeg-macos-x64.zip"
        [[ -f "$archive" ]] || download_file "https://evermeet.cx/ffmpeg/getrelease/zip" "$archive"
      fi
      unzip -q -o "$archive" -d "$extract"
      ;;
    *) echo "Unsupported FFMPEG platform: $platform" >&2; rm -rf "$extract"; return 2 ;;
  esac

  source="$(find "$extract" -type f -name "$name" -print -quit)"
  if [[ -z "$source" ]]; then
    echo "The downloaded FFmpeg archive did not contain $name ($platform)." >&2
    rm -rf "$extract"
    return 2
  fi
  cp "$source" "$platform_dir/$name"
  if [[ "$platform" == "windows-x64" ]]; then
    probe="$(find "$extract" -type f -name ffprobe.exe -print -quit)"
  else
    probe="$(find "$extract" -type f -name ffprobe -print -quit)"
  fi
  [[ -n "$probe" ]] && cp "$probe" "$platform_dir/$(basename "$probe")"
  chmod +x "$platform_dir/$name"
  for metadata_name in FFmpeg-LICENSE.txt FFmpeg-README.txt GPLv3.txt LICENSE readme.txt README.txt; do
    metadata="$(find "$extract" -type f -iname "$metadata_name" -print -quit)"
    if [[ -n "$metadata" ]]; then
      case "$metadata_name" in
        *LICENSE*|GPLv3.txt) cp "$metadata" "$platform_dir/FFmpeg-LICENSE.txt" ;;
        *) cp "$metadata" "$platform_dir/FFmpeg-README.txt" ;;
      esac
    fi
  done
  rm -rf "$extract"
}

stage_ffmpeg() {
  local platform="$1"
  local source destination_dir name metadata metadata_source search_root
  name="ffmpeg"
  [[ "$platform" == "windows-x64" ]] && name="ffmpeg.exe"
  source="$(find_ffmpeg "$platform")"
  if [[ -z "$source" && "$FETCH_FFMPEG" != "0" ]]; then
    fetch_ffmpeg "$platform"
    source="$(find_ffmpeg "$platform")"
  fi
  if [[ -z "$source" ]]; then
    echo "FFmpeg was not packaged for $platform. Set FFMPEG_PLATFORMS to a smaller list only when intentionally building a partial package." >&2
    return 2
  fi
  destination_dir="$ROOT_DIR/ADOFAIRenderer/bin/Release/FFmpeg/$platform"
  mkdir -p "$destination_dir"
  cp "$source" "$destination_dir/$name"
  if [[ "$platform" == "$FFMPEG_PLATFORM" ]]; then
    release_root="$ROOT_DIR/ADOFAIRenderer/bin/Release"
    root_target="$release_root/$name"
    # On a case-insensitive filesystem (for example Windows-mounted WSL),
    # the Unix root name `ffmpeg` resolves to the `FFmpeg/` directory itself.
    # The platform directory above is the authoritative copy in that case.
    if [[ "$name" != "ffmpeg" || ! -d "$root_target" ]]; then
      cp "$source" "$root_target"
      chmod +x "$root_target"
    fi
  fi
  chmod +x "$destination_dir/$name"
  if [[ "$platform" == "windows-x64" ]]; then
    metadata_source="$(dirname "$source")/ffprobe.exe"
    [[ -f "$metadata_source" ]] && cp "$metadata_source" "$destination_dir/ffprobe.exe"
  else
    metadata_source="$(dirname "$source")/ffprobe"
    [[ -f "$metadata_source" ]] && cp "$metadata_source" "$destination_dir/ffprobe"
  fi
  for metadata_name in FFmpeg-LICENSE.txt FFmpeg-README.txt; do
    metadata=""
    for search_root in "$(dirname "$source")" "$(dirname "$(dirname "$source")")" "$ROOT_DIR/packages/ffmpeg"; do
      if [[ -f "$search_root/$metadata_name" ]]; then metadata="$search_root/$metadata_name"; break; fi
    done
    if [[ -n "$metadata" ]]; then
      cp "$metadata" "$destination_dir/$metadata_name"
      if [[ "$platform" == "$FFMPEG_PLATFORM" ]]; then
        cp "$metadata" "$ROOT_DIR/ADOFAIRenderer/bin/Release/$metadata_name"
      fi
    fi
  done
}

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

IFS=',' read -r -a FFMPEG_PLATFORM_LIST <<< "$FFMPEG_PLATFORMS"
for platform in "${FFMPEG_PLATFORM_LIST[@]}"; do
  case "$platform" in
    windows-x64|linux-x64|macos-x64|macos-arm64) stage_ffmpeg "$platform" ;;
    *) echo "Unsupported FFMPEG platform in FFMPEG_PLATFORMS: $platform" >&2; exit 2 ;;
  esac
done

echo "Mod output: $ROOT_DIR/ADOFAIRenderer/bin/Release"
echo "Install the output folder under the game's Mods directory."
