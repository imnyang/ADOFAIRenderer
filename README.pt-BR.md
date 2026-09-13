# ADOFAI Renderer

O ADOFAI Renderer é um mod para Unity Mod Manager que renderiza fases personalizadas do ADOFAI em MP4 com resolução e FPS definidos.

## Recursos

- Pressione `F6` para renderizar a fase personalizada aberta.
- Janela de progresso centralizada com FPS, multiplicador em tempo real, ETA e horário previsto de conclusão.
- Perfis Preview, FullHD, QHD, UHD 4K e Custom.
- Resolução, 15–240 FPS, bitrate CBR de 1–200 Mbps, atraso final, áudio e pasta de saída configuráveis.
- NVENC selecionado automaticamente em GPUs NVIDIA, com fallback para `libx264`.
- Captura opcional do áudio do jogo e mux final de áudio/vídeo.
- BGA Mode oculta tiles, holds, efeitos dos tiles, planetas, partículas dos planetas e sons de hit do gameplay.
- RPC local com opção `bgaMode` por tarefa.

## Instalação

Copie todo o conteúdo da pasta Release para:

```text
A Dance of Fire and Ice/Mods/ADOFAIRenderer/
```

Mantenha `ADOFAIRenderer.dll`, `Info.json` e `ffmpeg.exe` na mesma pasta. Distribua também os arquivos de licença e README do FFmpeg. Ative o mod no Unity Mod Manager, abra uma fase personalizada, configure as opções e pressione `F6`.

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
| Video encoder | Auto | Auto / NvidiaNvenc / Software |
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
.\build.ps1 -FetchFFmpeg -Test
```

Use `-GameDir` para uma instalação do jogo fora do caminho padrão e `-MSBuildPath` para escolher o MSBuild.

## Licença

O código do projeto usa a licença MIT em [ADOFAIRenderer/LICENSE.md](ADOFAIRenderer/LICENSE.md). ADOFAI, Unity, Unity Mod Manager e FFmpeg permanecem sob suas próprias licenças.
