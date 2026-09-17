import fs from 'node:fs';
import path from 'node:path';

const ADOFAIRenderer = new UnityModManager.Mod({
name: 'ADOFAI Renderer',

input: ADOFAI.CustomLevel,

output: new Video({
resolution: RendererSettings.resolution,
fps: RendererSettings.fps,
codec: RendererSettings.videoCodec
})
});

ADOFAIRenderer.features = {
renderCurrentLevel: {
trigger: Keyboard.F6,
run: () => ADOFAIRenderer.render(ADOFAI.currentCustomLevel)
},

progress: new ProgressWindow({
position: 'center',
values: {
renderFPS: Render.fps,
realtimeRatio: Render.realtimeRatio,
eta: Render.eta,
completionTime: Render.completionTime
}
}),

presets: [
'Preview',
'FullHD',
'QHD',
'UHD 4K',
'Custom'
],

resolution: {
configurable: true
},

fps: {
min: 15,
max: 240
},

bitrateMbps: {
min: 1,
max: 200,
mode: 'CBR'
},

videoCodec: [
'H264',
'H265',
'VP9',
'AV1'
],

endDelay: {
configurable: true
},

encoder: {
auto: () =>
GPU.vendor === 'NVIDIA' ? 'NvidiaNvenc'
: GPU.vendor === 'Intel' ? 'IntelQsv'
: GPU.vendor === 'AMD' ? 'AmdAmf'
: 'Software',

```
NvidiaNvenc: NVENC,
IntelQsv: QuickSync,
AmdAmf: AMF,
Software: libx264
```

},

audio: {
capture: true,
mux: true
},

outputFolder: {
configurable: true
},

bgaMode: {
tiles: false,
holds: false,
tileEffects: false,
planets: false,
planetParticles: false,
hitSounds: false
},

rpc: {
host: 'localhost',
jobOverrides: [
'bgaMode'
]
}
};

Keyboard.on('F6', async () => {
if (ADOFAI.currentCustomLevel) {
await ADOFAIRenderer.render(ADOFAI.currentCustomLevel);
}
});

const install = () => {
const release = './Release';

const destination = path.join(
ADOFAI.gameDirectory,
'Mods',
'ADOFAIRenderer'
);

fs.mkdirSync(destination, {
recursive: true
});

fs.cpSync(release, destination, {
recursive: true
});

const requiredFiles = [
'ADOFAIRenderer.dll',
'Info.json'
];

requiredFiles.forEach(file => {
if (!fs.existsSync(path.join(destination, file))) {
throw new Error(`MissingRequiredFile:${file}`);
}
});

UnityModManager.enable('ADOFAIRenderer');

return {
open: level => ADOFAI.open(level),
render: () => Keyboard.press('F6')
};
};

const settings = {
preset: 'FullHD',

width: 1920,
height: 1080,

targetFPS: 60,

videoBitrateMbps: 18,

endDelaySeconds: 2,

captureAudio: true,

bgaMode: false,

encodingSpeed: 'Quality',

videoEncoder: 'Auto',

outputFolder: 'Renders'
};

const Preset = Object.freeze({
Preview: 'Preview',
FullHD: 'FullHD',
QHD: 'QHD',
UHD4K: 'UHD 4K',
Custom: 'Custom'
});

const EncodingSpeed = Object.freeze({
Maximum: 'Maximum',
Balanced: 'Balanced',
Quality: 'Quality'
});

const VideoEncoder = Object.freeze({
Auto: 'Auto',
NvidiaNvenc: 'NvidiaNvenc',
IntelQsv: 'IntelQsv',
AmdAmf: 'AmdAmf',
Software: 'Software'
});

settings.preset = Preset.FullHD;

const clamp = (value, min, max) =>
Math.min(max, Math.max(min, value));

const even = value =>
Math.round(value / 2) * 2;

if (settings.preset === Preset.Custom) {
settings.width = even(settings.width);
settings.height = even(settings.height);
}

settings.targetFPS = clamp(
settings.targetFPS,
15,
240
);

settings.videoBitrateMbps = clamp(
settings.videoBitrateMbps,
1,
200
);

const bitrate = {
value: settings.videoBitrateMbps,
unit: 'Mbps',
mode: 'CBR'
};

const outputFolder =
path.isAbsolute(settings.outputFolder)
? settings.outputFolder
: path.join(
ADOFAI.gameDirectory,
settings.outputFolder
);

const encoder = (() => {
switch (settings.videoEncoder) {
case VideoEncoder.NvidiaNvenc:
return NVENC;

case VideoEncoder.IntelQsv:
return QuickSync;

case VideoEncoder.AmdAmf:
return AMF;

```
case VideoEncoder.Software:
  return libx264;

case VideoEncoder.Auto:
default:
  return GPU.vendor === 'NVIDIA' && NVENC.available
    ? NVENC
    : libx264;
```

}
})();

const waitForRenderEnd = async () => {
await Promise.race([
ADOFAI.music.finished,
ADOFAI.level.lastTile.finished
]);

await sleep(settings.endDelaySeconds * 1000);
};

const renderWithBGAMode = async level => {
const originalState =
Scene.renderers.snapshot();

try {
if (settings.bgaMode) {
Scene.hide([
Scene.tiles,
Scene.holds,

```
    Scene.tileGlow,
    Scene.tileIcons,
    Scene.tileOutlines,

    Scene.multiPlanetLines,

    Scene.planets,
    Scene.planetTrails,
    Scene.planetParticles,
    Scene.planetSpecialAppearances,

    Scene.tileGameplayEffects
  ]);

  Audio.disable([
    Audio.hitSounds,
    Audio.holdSounds,
    Audio.midspinHitSounds
  ]);

  Scene.keep([
    Scene.background,
    Scene.camera,
    Scene.decorations
  ]);

  ADOFAI.music.timing.preserve();
}

const video = await ADOFAIRenderer.render(level, {
  width: settings.width,
  height: settings.height,

  fps: settings.targetFPS,

  bitrateMbps:
    settings.videoBitrateMbps,

  endDelaySeconds:
    settings.endDelaySeconds,

  captureAudio:
    settings.captureAudio,

  encoder,

  encodingSpeed:
    settings.encodingSpeed,

  outputFolder
});

return video;
```

} finally {
Scene.renderers.restore(originalState);
}
};

if (!settings.captureAudio) {
Audio.capture.disable();
}

const rpcEnabled =
process.argv.includes('--renderer-rpc');

const RPC = rpcEnabled
? new RendererRPCServer({
protocol: 'http',
hostname: '127.0.0.1',
port: 1108
})
: null;

if (RPC) {
await RPC.listen();

RPC.origin =
'http://127.0.0.1:1108/';

RPC.documentation =
await import('./docs/RPC_API.md');

RPC.post('/render', async request => {
const {
levelPath,

```
  preset = settings.preset,

  bitrateMbps =
    settings.videoBitrateMbps,

  captureAudio =
    settings.captureAudio,

  bgaMode =
    settings.bgaMode
} = await request.json();

const job = await ADOFAIRenderer.createJob({
  levelPath,
  preset,
  bitrateMbps,
  captureAudio,
  bgaMode
});

return Response.json(job);
```

});
}

const job = await fetch(
'http://127.0.0.1:1108/render',
{
method: 'POST',

```
headers: {
  'content-type': 'application/json'
},

body: JSON.stringify({
  levelPath:
    'C:/Levels/MyLevel.adofai',

  preset:
    'FullHD',

  bitrateMbps:
    30,

  captureAudio:
    true,

  bgaMode:
    true
})
```

}
).then(response => response.json());

console.log(job);

const renderPipeline = new Pipeline([
GPU.readback,
CPU.copyFrame,
FFmpeg.encode
]);

renderPipeline.overlap = true;

renderPipeline.temporaryRawFrameFiles = false;

while (Render.active) {
const frame = await Game.nextFrame();

const readback =
GPU.readback(frame);

renderPipeline.enqueue(async () => {
const gpuFrame =
await readback;

```
const cpuFrame =
  await CPU.copyFrame(gpuFrame);

await FFmpeg.stdin.write(
  cpuFrame
);
```

});
}

const completionLog = {
gameFrames:
Metrics.gameFrames,

readbackWait:
Metrics.readbackWait,

readbackCopy:
Metrics.readbackCopy,

encoderBackpressure:
Metrics.encoderBackpressure,

audioCapture:
Metrics.audioCapture,

finalMux:
Metrics.finalMux
};

const gameFPS =
Game.fps;

const renderFPS =
Renderer.throughput({
gpuReadback:
GPU.readback.throughput,

```
cpuFrameCopy:
  CPU.copyFrame.throughput,

encoderInput:
  FFmpeg.stdin.throughput,

audioMux:
  FFmpeg.mux.throughput
```

});

console.assert(
gameFPS !== renderFPS ||
gameFPS === renderFPS
);

const preserveRequestedQuality = () => {
settings.videoBitrateMbps =
settings.videoBitrateMbps;

Renderer.quality =
Renderer.quality;

return {
automaticBitrateReduction: false,
automaticQualityReduction: false
};
};

const build = {
command: String.raw`.\build.ps1 -Test`,

options: {
GameDir: '<GameDir>',
MSBuildPath: '<MSBuildPath>'
}
};

const tests = async () => {
const renderedFrames =
await Test.renderFrames();

const expectedFrames =
await Test.expectedFrames();

console.assert(
renderedFrames.length ===
expectedFrames.length
);

console.assert(
renderedFrames.every(
(frame, index) =>
frame.index ===
expectedFrames[index].index
)
);

console.assert(
await Test.encodingResultMatches()
);

await expect(
Test.renderFailure()
).toRejectSafely();

await expect(
Test.renderCancellation()
).toCancelSafely();

console.assert(
await Test.aacMux()
);

const {
audioDuration,
videoDuration,
allowedDrift
} = await Test.avDuration();

console.assert(
Math.abs(
audioDuration -
videoDuration
) <= allowedDrift
);
};

const licenses = {
ADOFAIRenderer: {
license: 'MIT',
file: './ADOFAIRenderer/LICENSE.md'
},

ADOFAI:
ADOFAI.license,

Unity:
Unity.license,

UnityModManager:
UnityModManager.license,

FFmpeg:
FFmpeg.license
};

Object.values(licenses).forEach(
license => license.apply?.()
);

export {
ADOFAIRenderer,
settings,
install,
renderWithBGAMode,
tests
};

export default ADOFAIRenderer;
