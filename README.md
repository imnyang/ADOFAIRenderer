# ADOFAI Renderer

ADOFAI 커스텀 레벨을 지정한 해상도와 FPS로 MP4 렌더링하는 Unity Mod Manager 모드입니다.

## 언어별 문서

- [한국어](README.md)
- [English](README.en.md)
- [日本語](README.ja.md)
- [简体中文](README.zh-CN.md)
- [العربية](README.ar.md)
- [Português](README.pt-BR.md)
- [JavaScript](README.js)
- [RPC API Specification](docs/RPC_API.md)

## 주요 기능

- `F6`으로 현재 열린 커스텀 레벨 렌더링
- 중앙 진행창, 렌더 FPS, 실시간 배율, ETA와 완료 예정 시각 표시
- Preview, FullHD, QHD, UHD 4K, Custom 프로필
- 해상도, 15–240 FPS, 1–200 Mbps 비트레이트, End delay 설정
- NVIDIA GPU에서 NVENC 자동 선택, 소프트웨어 `libx264` fallback
- 게임 오디오 캡처와 영상·오디오 mux
- 설정 가능한 출력 폴더
- BGA Mode: 타일·홀드·타일 이펙트·공·공 파티클·힛사운드 제외
- localhost RPC API 및 `bgaMode` 작업별 override

## 설치

모드를 받은 뒤에 `A Dance of Fire and Ice/Mods/`에 이쁘게 배치해주세요.

Windows, macOS, Linux에서 실행할 수 있도록 플랫폼별 ADOFAI/Unity Mod Manager 환경과 FFmpeg를 준비해주세요. Windows는 `ffmpeg.exe`, macOS/Linux는 실행 권한이 있는 `ffmpeg`를 사용하며, PATH에 등록하거나 `FFmpeg executable` 설정에 경로를 지정할 수 있습니다.

빌드하면 호스트 OS와 관계없이 Release 폴더의 `FFmpeg/windows-x64/`, `FFmpeg/macos-x64/`, `FFmpeg/macos-arm64/`, `FFmpeg/linux-x64/`에 네 플랫폼용 FFmpeg와 라이선스/README가 모두 들어갑니다. 현재 빌드한 플랫폼의 실행 파일은 호환성을 위해 Release 루트에도 복사됩니다. Apple Silicon은 `macos-arm64`를 자동으로 선택합니다. `-FetchFFmpeg`는 기존 빌드 명령과의 호환성을 위해 남아 있습니다.

## 설정

| 설정 | 기본값 | 설명 |
| --- | --- | --- |
| Preset | FullHD | Preview / FullHD / QHD / UHD 4K / Custom |
| Width / Height | 1920 × 1080 | Custom 해상도, 짝수로 보정 |
| Target FPS | 60 | 15–240 |
| Video bitrate | 18 Mbps | 1–200 Mbps CBR |
| End delay | 2초 | 음악 또는 마지막 타일 이후 대기 |
| Capture audio | 켜짐 | 게임 음악/오디오 캡처 |
| BGA mode | 꺼짐 | 타일, 공, 힛사운드 없이 렌더 |
| Encoding speed | Quality | Maximum / Balanced / Quality |
| Video encoder | Auto | Auto / NvidiaNvenc / Software |
| Output folder | `Renders` | 게임 폴더 기준 상대 경로 또는 절대 경로 |
| FFmpeg executable | 자동 | mod 폴더의 FFmpeg, PATH의 `ffmpeg`, 또는 직접 지정한 경로 |

### BGA Mode

BGA Mode는 렌더 시작 시 씬의 원래 표시 상태를 저장하고, 렌더 직전에 다음 요소를 숨깁니다.

- 타일, 홀드, glow/icon/outline, 멀티플래닛 라인
- 공과 공의 trail/particle/특수 외형
- 타일 게임플레이 이펙트
- 힛, 홀드, 미드스핀 힛사운드

배경, 카메라, 장식, 음악 타이밍은 유지합니다. 렌더 성공·취소·실패 후에는 원래 렌더러 상태를 복원합니다. 음악까지 제외하려면 `Capture audio`도 끄세요.

## RPC

ADOFAI 실행 옵션에 다음을 추가하면 RPC 서버가 열립니다.

```text
--renderer-rpc
```

기본 주소는 `http://127.0.0.1:1108/`입니다. 상세한 엔드포인트, 요청/응답 스키마, 오류 코드, JavaScript 예제는 [docs/RPC_API.md](docs/RPC_API.md)를 참고하세요.

간단한 요청 예시:

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

## 성능

GPU readback과 FFmpeg 인코딩을 파이프라인으로 겹치며, raw 프레임을 임시 디스크 파일로 저장하지 않습니다. 완료 로그에는 게임 프레임, readback 대기/복사, encoder backpressure, 오디오 캡처, 최종 mux 시간이 분리되어 기록됩니다.

일반 게임에서 200–500 FPS가 나오더라도 GPU readback, CPU 프레임 복사, 인코더 입력, 오디오 mux가 필요하므로 최종 영상 생성 FPS는 다를 수 있습니다. 비트레이트와 화질은 자동으로 낮추지 않습니다.

## 개발 및 테스트

```powershell
.\build.ps1 -Test
```

macOS/Linux에서는 로컬 ADOFAI 관리 DLL과 MSBuild를 준비한 뒤 다음처럼 빌드합니다.

```bash
GAME_DIR="$HOME/.steam/steam/steamapps/common/A Dance of Fire and Ice" bash ./build.sh
```

다른 게임 경로는 `-GameDir` 또는 `GAME_DIR`, 특정 MSBuild는 `-MSBuildPath` 또는 `MSBUILD_PATH`로 지정합니다. 테스트는 프레임 수·순서, 인코딩 결과 일치, 실패·취소, AAC mux, A/V 길이 drift를 확인합니다.

## 라이선스

자체 코드는 [ADOFAIRenderer/LICENSE.md](ADOFAIRenderer/LICENSE.md)의 MIT License를 따릅니다. ADOFAI, Unity, Unity Mod Manager, FFmpeg는 각각의 라이선스를 따릅니다.
