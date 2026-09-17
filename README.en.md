# ADOFAI Renderer

ADOFAI Renderer is a Unity Mod Manager mod that renders ADOFAI custom levels to video at a selected resolution and frame rate.

## Features

- Press `F6` to render the currently opened custom level.
- Centered progress window with FPS, realtime multiplier, ETA, and finish time.
- Preview, FullHD, QHD, UHD 4K, and Custom profiles.
- Configurable resolution, 15–240 FPS, 1–200 Mbps CBR bitrate, end delay, audio, and output directory.
- Selectable H.264/AVC, H.265/HEVC, VP9, and AV1 codecs (VP9 outputs WebM; the others output MP4).
- Selectable NVIDIA NVENC, Intel Quick Sync, AMD AMF, and software backends with GPU auto-detection.
- Optional game-audio capture and final audio/video mux.
- BGA Mode hides tiles, holds, tile effects, planets, planet particles, and gameplay hit sounds while preserving the background, camera, decorations, and music timing.
- Localhost RPC API with per-job `bgaMode` override.

## Installation

After downloading the mod, place it neatly in the game's `Mods` directory:

```text
A Dance of Fire and Ice/Mods/ADOFAIRenderer/
```

Install `ADOFAIRenderer.dll` and `Info.json` in the mod folder. On first launch with an internet connection, the mod downloads the FFmpeg binary for the current platform into `FFmpeg/<platform>`; existing installations or an explicit `FFmpeg executable` setting are respected. Enable the mod in Unity Mod Manager, open a custom level, configure the settings, and press `F6`.

Runtime support is intended for Windows, macOS, and Linux when the platform has a compatible ADOFAI and Unity Mod Manager environment. Windows uses `ffmpeg.exe`; macOS/Linux use an executable `ffmpeg` available on PATH or selected in the `FFmpeg executable` setting.

The build output does not contain FFmpeg. When the mod runs, it automatically selects and downloads one binary: `windows-x64`, `linux-x64`, `macos-x64`, or `macos-arm64` for Apple Silicon.

## Automatic updates

On startup, the mod checks GitHub's latest stable release. Drafts and pre-releases are excluded through `releases/latest`; the downloaded ZIP's version and SHA-256 are verified. When no render is active, the mod hot-reloads through Unity Mod Manager without restarting the game; if hot reload is unavailable, it falls back to applying the update after the game exits.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| Preset | FullHD | Preview / FullHD / QHD / UHD 4K / Custom |
| Width / Height | 1920 × 1080 | Custom resolution, normalized to even values |
| Target FPS | 60 | 15–240 |
| Video bitrate | 18 Mbps | 1–200 Mbps CBR |
| End delay | 2 seconds | Delay after the later of music or final tile |
| Capture audio | On | Capture game audio |
| BGA mode | Off | Render without tiles, planets, or hit sounds |
| Encoding speed | Quality | Maximum / Balanced / Quality |
| Video encoder | Auto | Auto / NvidiaNvenc / IntelQsv / AmdAmf / Software |
| Video codec | H264 | H264 / H265 / VP9 / AV1 |
| Video bit depth | 8-bit | 8-bit / 10-bit (`yuv420p10le`) |
| Output folder | `Renders` | Relative to the game folder or absolute |
| FFmpeg executable | Automatic | Auto-installed executable, PATH lookup, or an explicit path |

The software AV1 encoder does not support strict CBR. Audio renders use target-bitrate VBR, while video-only renders use capped CRF.

Before rendering, the selected encoder is verified with a real one-frame smoke test. If a hardware encoder fails, the renderer asks for consent before using Software for that render; declining leaves the saved setting unchanged and cancels the render.

### Diagnostics

Use `Run diagnostics` in the settings screen before rendering to check:

- whether FFmpeg starts and which version is installed;
- whether the selected video encoder is available;
- whether the selected video/audio encoders and container are available;
- whether the selected encoder passes an actual one-frame smoke test, plus GPU driver details;
- whether the output folder can be created and written to; and
- Unity audio output and GPU readback status.

Use `Copy report` to copy the result when reporting a problem. Diagnostics create a temporary file in the output folder and delete it immediately.

### BGA Mode

BGA Mode saves the original renderer state, hides gameplay-only visuals immediately before camera rendering, skips hit-time sound scheduling, and restores the original state after completion, cancellation, or failure. Music, background, camera motion, and decorations remain active. Disable `Capture audio` as well if the music should also be excluded.

## RPC

Start ADOFAI with:

```text
--renderer-rpc
```

The default base URL is `http://127.0.0.1:1108/`. See the complete [RPC API specification](docs/RPC_API.md) for endpoints, schemas, status codes, and JavaScript examples.

```js
const job = await fetch('http://127.0.0.1:1108/render', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    levelPath: 'C:/Levels/MyLevel.adofai',
    preset: 'FullHD',
    bitrateMbps: 30,
    captureAudio: true,
    bgaMode: true
  })
}).then(response => response.json());

console.log(job);
```

## Performance

GPU readback and FFmpeg encoding are pipelined without spooling raw frames to temporary disk files. Completion logs separate game-frame time, readback wait/copy time, encoder backpressure, audio capture, and final mux time. Bitrate and quality are not silently reduced.

Normal gameplay FPS and final render FPS are different measurements because every output frame still needs GPU readback, CPU copying, encoding input, and optional audio muxing.

## Build and test

```powershell
.\build.ps1 -Test
```

On macOS/Linux, use the portable build entry point after installing the local ADOFAI managed assemblies and MSBuild:

```bash
GAME_DIR="$HOME/.steam/steam/steamapps/common/A Dance of Fire and Ice" bash ./build.sh
```

Use `-GameDir`/`GAME_DIR` for a non-default installation and `-MSBuildPath`/`MSBUILD_PATH` for a specific MSBuild executable.

## License

Project code is MIT licensed in [ADOFAIRenderer/LICENSE.md](ADOFAIRenderer/LICENSE.md). ADOFAI, Unity, Unity Mod Manager, and FFmpeg remain under their respective licenses.
