# ADOFAI Renderer

ADOFAI Renderer is a Unity Mod Manager mod that renders ADOFAI custom levels to MP4 at a selected resolution and frame rate.

## Features

- Press `F6` to render the currently opened custom level.
- Centered progress window with FPS, realtime multiplier, ETA, and finish time.
- Preview, FullHD, QHD, UHD 4K, and Custom profiles.
- Configurable resolution, 15–240 FPS, 1–200 Mbps CBR bitrate, end delay, audio, and output directory.
- Automatic NVENC selection on NVIDIA GPUs with software `libx264` fallback.
- Optional game-audio capture and final audio/video mux.
- BGA Mode hides tiles, holds, tile effects, planets, planet particles, and gameplay hit sounds while preserving the background, camera, decorations, and music timing.
- Localhost RPC API with per-job `bgaMode` override.

## Installation

Copy the complete Release folder to:

```text
A Dance of Fire and Ice/Mods/ADOFAIRenderer/
```

Keep `ADOFAIRenderer.dll`, `Info.json`, and `ffmpeg.exe` together. The FFmpeg license/readme files should also be distributed with the executable. Enable the mod in Unity Mod Manager, open a custom level, configure the settings, and press `F6`.

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
| Video encoder | Auto | Auto / NvidiaNvenc / Software |
| Output folder | `Renders` | Relative to the game folder or absolute |

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
.\build.ps1 -FetchFFmpeg -Test
```

Use `-GameDir` for a non-default ADOFAI installation and `-MSBuildPath` for a specific MSBuild executable.

## License

Project code is MIT licensed in [ADOFAIRenderer/LICENSE.md](ADOFAIRenderer/LICENSE.md). ADOFAI, Unity, Unity Mod Manager, and FFmpeg remain under their respective licenses.
