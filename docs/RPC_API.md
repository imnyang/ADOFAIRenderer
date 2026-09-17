# OrbitRender RPC API Specification

Version: `1.2.0`

This document defines the localhost RPC API provided by OrbitRender. The server is disabled by default. Enable it by adding the following argument when launching ADOFAI:

```text
--renderer-rpc
```

The default port is `1108`. Use the following argument to select another port:

```text
--renderer-rpc-port=12000
```

## 1. Transport

| Item | Value |
| --- | --- |
| Protocol | HTTP/1.1 |
| Bind address | `127.0.0.1` |
| Default port | `1108` |
| Default base URL | `http://127.0.0.1:1108` |
| Request body | UTF-8 JSON |
| Response body | UTF-8 JSON, except video downloads |
| CORS | `Access-Control-Allow-Origin: *` |

The server binds only to the loopback address. Do not expose it through an unauthenticated external proxy: `levelPath` allows the server to read a local file and the API controls the game renderer.

## 2. Endpoints

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/health` | Check server and renderer state |
| `GET` | `/jobs` | List all jobs known to the current process |
| `POST` | `/render` | Create a render job |
| `GET` | `/render/{id}` | Read one job's status |
| `GET` | `/render/{id}/download` | Download a completed video |
| `POST`, `DELETE` | `/render/{id}/cancel` | Request cancellation |
| `OPTIONS` | `/*` | CORS preflight |

Unknown paths return `404` with a JSON error object.

## 3. Health

### Request

```http
GET /health HTTP/1.1
Host: 127.0.0.1:1108
```

### Response: `200 OK`

```json
{
  "ok": true,
  "renderer": "idle"
}
```

`renderer` is the current `RenderState` converted to lowercase. Typical values are `idle`, `preparing`, `rendering`, `finishing`, `completed`, `failed`, and `cancelled`.

## 4. Create a render job

### Request

```http
POST /render HTTP/1.1
Host: 127.0.0.1:1108
Content-Type: application/json

{
  "levelPath": "C:/Levels/MyLevel.adofai",
  "preset": "FullHD",
  "fps": 60,
  "bitrateMbps": 30,
  "captureAudio": true,
  "bgaMode": true,
  "showPlanetRings": true,
  "showSongTitle": true,
  "showCountdown": true,
  "showResultText": true,
  "endDelaySeconds": 2
}
```

Exactly one of `levelPath` or the compatibility alias `path` must provide a valid existing file. The path is resolved on the computer running the game; the server reads the file locally.

### Request fields

| Field | Type | Required | Range / values | Description |
| --- | --- | ---: | --- | --- |
| `levelPath` | string | Conditional | Existing file | Path to the ADOFAI level file |
| `path` | string | Conditional | Existing file | Alias for `levelPath` |
| `preset` | string | No | `Custom`, `Preview`, `FullHD`, `QHD`, `UHD4K` | Video preset |
| `width` | integer | No | `320..3840` | Video width; normalized to an even value |
| `height` | integer | No | `180..2160` | Video height; normalized to an even value |
| `fps` | integer | No | `15..240` | Output FPS |
| `targetFps` | integer | No | `15..240` | Alias for `fps` |
| `bitrateMbps` | integer | No | `1..200` | CBR video bitrate in Mbps |
| `bitrate` | integer | No | `1..200` | Alias for `bitrateMbps` |
| `videoCodec` | string | No | `H264`, `H265`, `VP9`, `AV1` | Video codec; VP9 produces WebM and the others produce MP4 |
| `codec` | string | No | Same as `videoCodec` | Compatibility alias for `videoCodec` |
| `bitDepth` | integer | No | `8` or `10` | Output video bit depth; `10` uses `yuv420p10le` |
| `endDelaySeconds` | number | No | `0..30` | Delay after the music/final-tile end time |
| `captureAudio` | boolean | No | `true` / `false` | Capture game audio |
| `audio` | boolean | No | `true` / `false` | Alias for `captureAudio` |
| `bgaMode` | boolean | No | `true` / `false` | Exclude tiles, planets, and gameplay hit sounds |
| `showPlanetRings` | boolean | No | `true` / `false` | Include planet orbit rings |
| `showSongTitle` | boolean | No | `true` / `false` | Include the default song-title text |
| `showCountdown` | boolean | No | `true` / `false` | Include Get Ready, countdown numbers, and Go text |
| `showResultText` | boolean | No | `true` / `false` | Include only the completion/Pure Perfect message; judgment details remain hidden |

Omitted options use the Unity Mod Manager settings, including BGA mode and all visible-component options.

If both `fps` and `targetFps` are supplied, they must match. The same applies to `bitrateMbps` and `bitrate`. When both audio fields are supplied, `captureAudio` takes precedence. Supplying video overrides without `preset` uses the Custom profile.

H.264, H.265, and AV1 outputs use MP4 with AAC audio. VP9 outputs WebM with Opus audio. The selected codec must be present in the configured FFmpeg build. Software AV1 uses `libaom-av1`; it uses target-bitrate VBR when audio is included and constant-quality mode for video-only renders.

The encoder setting selects NVIDIA NVENC, Intel Quick Sync (`*_qsv`), AMD AMF (`*_amf`), or software encoding where the selected codec has a matching backend. VP9 has no matching NVENC/QSV/AMF encoder in this profile and uses `libvpx-vp9`.

### Accepted response: `202 Accepted`

```json
{
  "id": "c0ffee0123456789abcdef0123456789",
  "state": "queued",
  "statusUrl": "/render/c0ffee0123456789abcdef0123456789",
  "downloadUrl": "/render/c0ffee0123456789abcdef0123456789/download"
}
```

Jobs run asynchronously. Poll `statusUrl` until the job reaches `completed`, `failed`, or `cancelled`.

## 5. Job status

### Request

```http
GET /render/{id} HTTP/1.1
Host: 127.0.0.1:1108
```

### Response: `200 OK`

```json
{
  "id": "c0ffee0123456789abcdef0123456789",
  "state": "rendering",
  "levelPath": "C:/Levels/MyLevel.adofai",
  "settings": {
    "preset": "FullHD",
    "width": null,
    "height": null,
    "fps": 60,
    "bitrateMbps": 30,
    "endDelaySeconds": 2,
    "bgaMode": true
  },
  "outputPath": "C:/Games/ADOFAI/Renders/Render_MyLevel_2026-01-01_12-00-00_a1b2c3.mp4",
  "totalFrames": 7201,
  "capturedFrames": 3600,
  "progress": 0.4999,
  "error": null,
  "updatedUtc": "2026-01-01T03:00:00.0000000Z"
}
```

### Job states

```text
queued → loading → preparing → rendering → finishing → completed
                                                     ├→ failed
                                                     └→ cancelled
```

| State | Meaning |
| --- | --- |
| `queued` | Accepted by RPC and waiting for game processing |
| `loading` | Loading the level editor and level file |
| `preparing` | Preparing the camera, audio, FFmpeg, encoder smoke test, and render state |
| `rendering` | Generating video frames |
| `finishing` | Draining readbacks, finalizing FFmpeg, and muxing audio |
| `awaitingconfirmation` | Hardware encoder failed its smoke test and is waiting for user approval to use Software for this render |
| `completed` | The final selected video is ready |
| `failed` | The job ended with an error; `error` contains the message |
| `cancelled` | The job was cancelled by the user or force-cancelled |

When `totalFrames` is zero, `progress` is `0.0`. Otherwise it is `capturedFrames / totalFrames`, clamped to `0..1`.

## 6. List jobs

### Request

```http
GET /jobs HTTP/1.1
Host: 127.0.0.1:1108
```

### Response: `200 OK`

The response is an array of job status objects.

```json
[
  {
    "id": "c0ffee0123456789abcdef0123456789",
    "state": "completed",
    "levelPath": "C:/Levels/MyLevel.adofai",
    "settings": { "bgaMode": true },
    "outputPath": "C:/Games/ADOFAI/Renders/Render_MyLevel.mp4",
    "totalFrames": 7201,
    "capturedFrames": 7201,
    "progress": 1.0,
    "error": null,
    "updatedUtc": "2026-01-01T03:05:00.0000000Z"
  }
]
```

The job list is held in process memory and is not persisted across server restarts.

## 7. Download a completed render

### Request

```http
GET /render/{id}/download HTTP/1.1
Host: 127.0.0.1:1108
```

For a completed job, the server returns the selected video with `Content-Type: video/mp4` or `video/webm` and an attachment `Content-Disposition`. A render cannot be downloaded before it completes.

| Situation | Status |
| --- | ---: |
| Output file is ready | `200 OK` |
| Job is not finished | `404 Not Found` |
| Job is terminal but has no output file | `409 Conflict` |
| Unknown job ID | `404 Not Found` |

## 8. Cancel a job

### Request

```http
POST /render/{id}/cancel HTTP/1.1
Host: 127.0.0.1:1108
```

`DELETE /render/{id}/cancel` has the same behavior.

### Response: `202 Accepted`

```json
{
  "id": "c0ffee0123456789abcdef0123456789",
  "state": "cancelling"
}
```

Cancellation is asynchronous. Read the status endpoint for the final state. Cancelling an already terminal job returns `409 Conflict`.

## 9. Error format

Error responses generally have this shape:

```json
{
  "error": "Level file does not exist: C:/Levels/Missing.adofai"
}
```

### Common status codes

| Status | Situation |
| ---: | --- |
| `400` | Missing level path, missing file, or invalid option |
| `404` | Unknown endpoint or job ID |
| `409` | Another render is running, terminal job cancellation, or missing terminal output |
| `413` | JSON request body is larger than 1 MiB |
| `500` | Unhandled server exception |

## 10. JavaScript client example

The following example works in Node.js 18+ or another runtime with a global `fetch` implementation.

```js
const base = 'http://127.0.0.1:1108';

async function renderBga(levelPath) {
  const create = await fetch(`${base}/render`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      levelPath,
      preset: 'FullHD',
      fps: 60,
      bitrateMbps: 30,
      captureAudio: true,
      bgaMode: true,
      endDelaySeconds: 2
    })
  });

  if (!create.ok) throw new Error(await create.text());
  const accepted = await create.json();

  while (true) {
    const status = await fetch(`${base}${accepted.statusUrl}`)
      .then(response => response.json());
    console.log(status.state, status.progress);

    if (status.state === 'completed') {
      return `${base}${accepted.downloadUrl}`;
    }
    if (['failed', 'cancelled'].includes(status.state)) {
      throw new Error(status.error || `Render ${status.state}`);
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
}

console.log(await renderBga('C:/Levels/MyLevel.adofai'));
```

## 11. Operational notes

- RPC enqueues render work onto the game's Unity main thread. A `202` response does not mean that level loading has finished.
- Only one render can run at a time.
- The output directory comes from the Unity Mod Manager settings. RPC does not provide an output-directory override.
- `bgaMode` excludes tiles, planets, and gameplay hit sounds; it does not disable the music. Send `captureAudio: false` to exclude captured audio as well.
- The server binds to loopback only, but any local process that can reach it may submit a local `levelPath` and control rendering. Keep RPC disabled in untrusted local environments.
