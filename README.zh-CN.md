# ADOFAI Renderer

ADOFAI Renderer 是一个 Unity Mod Manager 模组，可以将 ADOFAI 自定义关卡按照指定分辨率和帧率渲染为 MP4。

## 功能

- 按 `F6` 渲染当前打开的自定义关卡
- 居中的进度窗口，显示 FPS、实时倍率、ETA 和预计完成时间
- Preview、FullHD、QHD、UHD 4K 和 Custom 配置
- 可设置分辨率、15–240 FPS、1–200 Mbps CBR 码率、结束延迟、音频和输出目录
- NVIDIA GPU 自动优先使用 NVENC，必要时回退到软件 `libx264`
- 可选的游戏音频捕获和最终音视频 mux
- BGA Mode 隐藏瓦片、Hold、瓦片特效、星球、星球粒子以及游戏玩法击打音效
- localhost RPC API 支持每个任务单独设置 `bgaMode`

## 安装

下载模组后，请将它整齐地放入游戏的 `Mods` 目录：

```text
A Dance of Fire and Ice/Mods/ADOFAIRenderer/
```

请将 `ADOFAIRenderer.dll`、`Info.json` 和 FFmpeg 可执行文件放在同一目录，并同时分发 FFmpeg 的许可证和 README 文件。Windows 使用 `ffmpeg.exe`，macOS/Linux 使用有执行权限的 `ffmpeg`，也可以在 `FFmpeg executable` 设置中指定路径。在 Unity Mod Manager 中启用模组，打开自定义关卡，完成设置后按 `F6`。

只要平台提供兼容的 ADOFAI 和 Unity Mod Manager 环境，Windows、macOS 和 Linux 都可以运行。

普通构建无论在哪个系统上执行，都会在 Release 文件夹的 `FFmpeg/windows-x64/`、`FFmpeg/macos-x64/`、`FFmpeg/macos-arm64/` 和 `FFmpeg/linux-x64/` 中包含四个平台的 FFmpeg 以及许可证/README 文件。Apple Silicon 会自动选择 `macos-arm64`。`-FetchFFmpeg` 仍可用于兼容旧的构建命令。

## 设置

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| Preset | FullHD | Preview / FullHD / QHD / UHD 4K / Custom |
| Width / Height | 1920 × 1080 | Custom 分辨率，会自动调整为偶数 |
| Target FPS | 60 | 15–240 |
| Video bitrate | 18 Mbps | 1–200 Mbps CBR |
| End delay | 2 秒 | 音乐或最后一块瓦片结束后的等待时间 |
| Capture audio | 开启 | 捕获游戏音频 |
| BGA mode | 关闭 | 不包含瓦片、星球和击打音效 |
| Encoding speed | Quality | Maximum / Balanced / Quality |
| Video encoder | Auto | Auto / NvidiaNvenc / Software |
| Output folder | `Renders` | 相对于游戏目录的路径或绝对路径 |

### BGA Mode

BGA Mode 会保存原始渲染器状态，在摄像机渲染前隐藏游戏玩法视觉元素，并跳过击打音效调度。渲染完成、取消或失败后会恢复原始状态。背景、摄像机、装饰和音乐时间轴保持不变。如果还要排除音乐，请同时关闭 `Capture audio`。

## RPC

使用以下启动参数启动 ADOFAI：

```text
--renderer-rpc
```

默认地址为 `http://127.0.0.1:1108/`。完整端点、请求/响应结构、状态码和 JavaScript 示例请查看 [RPC API 规范](docs/RPC_API.md)。

```js
const job = await fetch('http://127.0.0.1:1108/render', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    levelPath: 'C:/Levels/MyLevel.adofai',
    preset: 'FullHD',
    bitrateMbps: 30,
    bgaMode: true,
    captureAudio: true
  })
}).then(response => response.json());

console.log(job);
```

## 构建与测试

```powershell
.\build.ps1 -Test
```

非默认游戏目录使用 `-GameDir`，指定 MSBuild 使用 `-MSBuildPath`。

## 许可证

项目代码使用 [ADOFAIRenderer/LICENSE.md](ADOFAIRenderer/LICENSE.md) 中的 MIT License。ADOFAI、Unity、Unity Mod Manager 和 FFmpeg 遵循各自的许可证。
