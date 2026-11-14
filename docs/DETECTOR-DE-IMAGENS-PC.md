# Detector de imagens do PC (beta) — 2026-11-10 (regra de decisão revista em 2026-11-14)

Espelha o detector do Android: olha a tela, passa por um modelo que roda no PC e trava a tela (escada 5 → 15 → 30 min) se parecer conteúdo adulto.
**Escrito sem .NET e sem TensorFlow no ambiente: nada foi compilado nem testado.** Rode `dotnet build -c Release` e mande os erros.

## Como funciona
- `App/Services/ImageGuard.cs`: a cada ~2 s, com a opção ligada, **bloqueio ativo ou proteções permanentes**, fora de uma trava e
  com o foco fora do próprio SharkBlocker, captura cada monitor e pede a pontuação.
- `App/Services/NsfwClassifier.cs`: carrega `nsfw\model.onnx` (ao lado do .exe) com o ONNX Runtime, reduz a imagem para 224x224
  e devolve **duas pontuações separadas**: *explícita* (`explicitIndices` = hentai + porn; hentai cobre anime/mangá adulto) e
  *sugestiva* (`suggestiveIndices` = sexy: biquíni, roupa sugestiva, mas também close de rosto e muito tom de pele), mais a saída completa para o log.
- `App/Services/ImageDecision.cs` (puro, sem WinForms/ONNX; testado em `tests/SharkBlocker.ParityTests/ImageDecisionTests.cs`): decide o nível da captura.
  Antes a soma hentai+porn+sexy >= 0,80 em **uma** captura travava; um close de rosto num vídeo de xadrez bastava. Agora:

  | Nível | Condição (Padrão) | Capturas seguidas |
  |---|---|---|
  | Imediato | explícito >= 0,96 | 1 (trava na hora) |
  | Explícito | explícito >= 0,85 | 2 |
  | Sugestivo | explícito + sexy >= 0,93 | 3 |

  Rigor (`BlockState.ImageSensitivity`, agora na aba Proteções): **Menos rígido** 0,92 · 0,97 · 0,98 (explícito · sugestivo · imediato),
  **Padrão** 0,85 · 0,93 · 0,96, **Mais rígido** 0,70 · 0,85 · 0,92. Aumentar o rigor não pede senha; **diminuir** passa por `UninstallFlow.TryAuthorizeRemoval`.
- Captura suspeita **não minimiza nada**: o laço só refaz a varredura em ~0,7 s (em vez de 2 s) para confirmar. A confirmação se perde se
  a próxima captura vier limpa, se o foco for o próprio SharkBlocker ou se entrar uma trava.
- Detecção **confirmada** minimiza as janelas (Win+M) e trava a tela, em **escada**: 1ª vez **5 min**, 2ª **15 min**,
  da 3ª em diante **30 min** (`StateManager.StartImageScreenLock`; contagem em `BlockState.ImageStrikes`, persiste ao reabrir o app e zera
  após 24 h sem detecções). **Carência:** nos 45 s depois de uma trava acabar, só minimiza (sem nova trava). Não olha a área de trabalho nem a
  barra de tarefas.
- Opção em Configurações › "Detector de imagens". **Ligar não pede senha; desligar passa por `UninstallFlow.TryAuthorizeRemoval`.**
- Nada é salvo nem enviado. O log guarda só números, por classe, em três tipos de linha (`C:\ProgramData\SharkBlocker\app.log`):
  - `Imagem: suspeita Explicit (1/2) explícito 0,88 sugestivo 0,03 | drawings 0,01 hentai 0,40 neutral 0,08 porn 0,48 sexy 0,03` (aguardando confirmação);
  - `Imagem: CONFIRMADA ...` (vai travar);
  - `Imagem: abaixo do limite, ...` (no máx. 1 a cada 10 s, só com explícito + sugestivo >= 0,50): **serve para calibrar** (ex.: o close do apresentador de xadrez).
  Para ajustar os limites com casos reais, mande essas linhas dos momentos em que travou sem motivo (ou deixou passar algo).

## Como obter o modelo
**Já vem pronto** no `pc-modelo-onnx.zip` (`App\Assets\nsfw\model.onnx` + `model.json`): é só extrair na pasta do projeto do PC e compilar.
Para gerar de novo (não precisa de TensorFlow; só numpy, protobuf e onnxruntime):
1. Extraia o zip `mobilenet_v2_140_224` do GantMan (tem `frozen_graph.pb`).
2. No PowerShell, na pasta do projeto do PC:
   ```
   py -3.11 -m venv venv-modelo
   .\venv-modelo\Scripts\activate
   pip install numpy protobuf onnxruntime
   python scripts\converter-modelo-onnx.py C:\caminho\mobilenet_v2_140_224
   ```
3. `dotnet build -c Release` (ou o `quick-install.ps1`), abra o app e confira "Modelo: Modelo carregado e pronto." (o detalhe mostra `explicitIndices=[1,3], suggestiveIndices=[4]`).
   O `model.json` antigo (`nsfwIndices:[1,3,4]`) ainda é lido: o índice 4 de um modelo de 5 classes vira "sugestivo".
4. **Teste só com imagens neutras ou limítrofes** (praia, esporte) para calibrar; não precisa de conteúdo explícito.
O conversor lê o grafo congelado direto (Conv, BatchNorm, Relu6, Add, AvgPool, MatMul, Softmax) e foi conferido contra um cálculo
independente em numpy (diferença máxima ~2e-6 em 4 imagens de teste). Não foi comparado com o TFLite do Android nem rodou no .NET.

## Limites honestos
- Janelas com proteção contra captura (alguns players/DRM) saem pretas: o detector não vê nada nelas.
- A imagem pode ficar visível 2 a 4 s antes da trava (confirmação em capturas seguidas; o "Imediato" trava em ~2 s).
- Modelos treinados em fotos erram em imagens de IA e ilustração/anime; ainda há falsos positivos e falsos negativos.
- **Biquíni/roupa sugestiva x close de rosto usam a mesma classe ("sexy")**: não dá para separá-los só com este modelo. O sugestivo tem limite alto e
  exige 3 capturas; isso evita o close de rosto, mas deixa passar biquíni com pontuação mais baixa (use "Mais rígido" e confira o log de calibração).
  Separar de verdade exige um detector por regiões (NudeNet, "Etapa 2", não implementada).
- Mangá em preto e branco costuma cair em "drawings" (seguro); se passar, o log mostra a pontuação para ajustar.
- Não fecha a janela: só minimiza tudo (Win+M). Se a imagem continuar aberta depois da carência, pode travar de novo.
- Consumo: uma captura + uma inferência por monitor a cada ~2 s.
- **O Watchdog NÃO compila `ImageGuard.cs`/`NsfwClassifier.cs`** (usam WinForms/WPF e ONNX): não os inclua em `Watchdog.csproj`.
- O instalador (`Installer.iss`) copia a pasta `publish` do App; a pasta `nsfw\` vai junto, e o ONNX Runtime nativo também.
