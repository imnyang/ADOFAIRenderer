# OrbitRender

O OrbitRender é um mod para Unity Mod Manager que renderiza fases personalizadas do ADOFAI em vídeo com resolução e FPS definidos.

## Recursos

- Pressione `F6` para renderizar a fase personalizada aberta.
- Janela de progresso centralizada com FPS, multiplicador em tempo real, ETA e horário previsto de conclusão.
- Perfis Preview, FullHD, QHD, UHD 4K e Custom.
- Resolução, 15–240 FPS, bitrate CBR de 1–200 Mbps, atraso final, áudio e pasta de saída configuráveis.
- Seleção dos codecs H.264/AVC, H.265/HEVC, VP9 e AV1 (VP9 gera WebM; os demais geram MP4).
- Seleção de NVIDIA NVENC, Intel Quick Sync, AMD AMF e software, com detecção automática da GPU.
- Captura opcional do áudio do jogo e mux final de áudio/vídeo.
- BGA Mode oculta tiles, holds, efeitos dos tiles, planetas, partículas dos planetas e sons de hit do gameplay.
- RPC local com opção `bgaMode` por tarefa.

## Instalação

Depois de baixar o mod, coloque-o corretamente na pasta `Mods` do jogo:

```text
A Dance of Fire and Ice/Mods/OrbitRender/
```

Coloque `OrbitRender.dll` e `Info.json` na pasta do mod. Na primeira execução, o mod baixa automaticamente o FFmpeg da plataforma atual para `FFmpeg/<platform>/`; uma instalação existente ou um caminho definido em `FFmpeg executable` será respeitado. Ative o mod no Unity Mod Manager, abra uma fase personalizada, configure as opções e pressione `F6`.

O runtime suporta Windows, macOS e Linux quando houver uma instalação compatível do ADOFAI e do Unity Mod Manager.

A saída da compilação não inclui FFmpeg. Ao executar, o mod seleciona e baixa automaticamente apenas uma versão: `windows-x64`, `linux-x64`, `macos-x64` ou `macos-arm64` para Apple Silicon.

## Atualizações automáticas

Ao iniciar, o mod verifica o lançamento estável mais recente no GitHub. O endpoint `releases/latest` exclui drafts e pre-releases, e a versão e o SHA-256 do ZIP são verificados. Quando não há uma renderização ativa, o update é aplicado via hot-reload do Unity Mod Manager sem reiniciar o jogo; se o hot-reload não estiver disponível, ele será aplicado após o fechamento do jogo.

## Configurações

| Configuração | Padrão | Descrição |
| --- | --- | --- |
| Preset | FullHD | Preview / FullHD / QHD / UHD 4K / Custom |
| Width / Height | 1920 × 1080 | Resolução Custom, ajustada para valores pares |
| Target FPS | 60 | 15–240 |
| Video bitrate | 18 Mbps | CBR de 1–200 Mbps |
| End delay | 2 segundos | Espera após a música ou o último tile |
| Capture audio | Ativado | Captura o áudio do jogo |
| BGA mode | Desativado | Renderiza sem tiles, planetas ou sons de hit |
| Encoding speed | Quality | Maximum / Balanced / Quality |
| Video encoder | Auto | Auto / NvidiaNvenc / IntelQsv / AmdAmf / Software |
| Video codec | H264 | H264 / H265 / VP9 / AV1 |
| Output folder | `Renders` | Caminho relativo à pasta do jogo ou caminho absoluto |

### BGA Mode

O BGA Mode salva o estado original dos renderizadores, oculta os elementos de gameplay antes da renderização da câmera, pula o agendamento dos sons de hit e restaura o estado após conclusão, cancelamento ou falha. O fundo, a câmera, as decorações e o tempo da música continuam ativos. Para remover a música também, desative `Capture audio`.

## RPC

Inicie o jogo com:

```text
--renderer-rpc
```

O endereço padrão é `http://127.0.0.1:1108/`. Consulte a [especificação da RPC API](docs/RPC_API.md) para endpoints, schemas, códigos de status e exemplos em JavaScript.

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

## Build e testes

```powershell
.\build.ps1 -Test
```

Use `-GameDir` para uma instalação do jogo fora do caminho padrão e `-MSBuildPath` para escolher o MSBuild.

## Licença

O código do projeto usa a licença MIT em [OrbitRender/LICENSE.md](OrbitRender/LICENSE.md). ADOFAI, Unity, Unity Mod Manager e FFmpeg permanecem sob suas próprias licenças.
