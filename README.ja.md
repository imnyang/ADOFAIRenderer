# ADOFAI Renderer

ADOFAI Renderer は、ADOFAI のカスタムレベルを指定した解像度とFPSで MP4 にレンダリングする Unity Mod Manager 用MODです。

## 主な機能

- `F6` で現在開いているカスタムレベルをレンダリング
- 中央配置の進捗ウィンドウ、FPS、リアルタイム倍率、ETA、完了予定時刻
- Preview、FullHD、QHD、UHD 4K、Custom プロファイル
- 解像度、15–240 FPS、1–200 Mbps CBRビットレート、終了後の待機時間、音声、出力先を設定可能
- NVIDIA GPUではNVENCを自動選択し、ソフトウェア `libx264` にフォールバック
- ゲーム音声のキャプチャと動画・音声のmux
- BGA Modeではタイル、ホールド、タイルエフェクト、惑星、惑星パーティクル、ゲームプレイのヒット音を非表示
- localhost RPC APIと、ジョブごとの `bgaMode` 上書き

## インストール

ダウンロードしたMODをゲームの `Mods` フォルダに配置します。

```text
A Dance of Fire and Ice/Mods/ADOFAIRenderer/
```

`ADOFAIRenderer.dll` と `Info.json` をModフォルダに置いてください。初回起動時に、現在のプラットフォーム用FFmpegが `FFmpeg/<platform>/` に自動ダウンロードされます。既存のインストールや `FFmpeg executable` 設定のパスはそのまま使用します。Unity Mod Managerで有効化し、カスタムレベルを開いて設定後、`F6`を押します。

対応環境は、互換性のあるADOFAIとUnity Mod Managerが利用できるWindows、macOS、Linuxです。

ビルド出力にはFFmpegを含めません。Mod実行時にWindowsは `windows-x64`、Linuxは `linux-x64`、Intel Macは `macos-x64`、Apple Siliconは `macos-arm64` を自動選択し、その1つだけをダウンロードします。

## 自動アップデート

起動時にGitHubの最新の正式リリースを確認します。`releases/latest` を使用するためDraftとプレリリースは対象外です。ダウンロードしたZIPのバージョンとSHA-256を検証し、レンダー中でなければUnity Mod Managerのhot-reloadでゲームを再起動せずに適用します。hot-reloadが利用できない場合はゲーム終了後に適用します。

## 設定

| 設定 | 初期値 | 説明 |
| --- | --- | --- |
| Preset | FullHD | Preview / FullHD / QHD / UHD 4K / Custom |
| Width / Height | 1920 × 1080 | Custom解像度。偶数に補正されます |
| Target FPS | 60 | 15–240 |
| Video bitrate | 18 Mbps | 1–200 Mbps CBR |
| End delay | 2秒 | 曲または最後のタイルの後の待機時間 |
| Capture audio | オン | ゲーム音声をキャプチャ |
| BGA mode | オフ | タイル、惑星、ヒット音なしでレンダリング |
| Encoding speed | Quality | Maximum / Balanced / Quality |
| Video encoder | Auto | Auto / NvidiaNvenc / Software |
| Output folder | `Renders` | ゲームフォルダ基準の相対パスまたは絶対パス |

### BGA Mode

BGA Modeは元のRenderer状態を保存し、カメラレンダリングの直前にゲームプレイ用のビジュアルを非表示にします。ヒット音のスケジュールも停止し、完了・キャンセル・失敗時には元の状態を復元します。背景、カメラ、装飾、音楽のタイミングは維持されます。音楽も除外する場合は `Capture audio` もオフにしてください。

## RPC

ADOFAIを次の引数で起動します。

```text
--renderer-rpc
```

標準のベースURLは `http://127.0.0.1:1108/` です。エンドポイント、スキーマ、ステータスコード、JavaScript例は [RPC API仕様](docs/RPC_API.md) を参照してください。

```js
const job = await fetch('http://127.0.0.1:1108/render', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    levelPath: 'C:/Levels/MyLevel.adofai',
    preset: 'FullHD',
    bgaMode: true,
    captureAudio: true
  })
}).then(response => response.json());

console.log(job);
```

## ビルドとテスト

```powershell
.\build.ps1 -Test
```

別のゲームフォルダには `-GameDir`、使用するMSBuildには `-MSBuildPath` を指定します。

## ライセンス

プロジェクトコードは [ADOFAIRenderer/LICENSE.md](ADOFAIRenderer/LICENSE.md) のMIT Licenseです。ADOFAI、Unity、Unity Mod Manager、FFmpegにはそれぞれのライセンスが適用されます。
