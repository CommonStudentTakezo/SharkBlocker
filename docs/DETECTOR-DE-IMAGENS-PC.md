# Detector de imagens do PC (beta) — 2026-11-10

Espelha o detector do Android: olha a tela, passa por um modelo que roda no PC e trava por 30 min se parecer conteúdo adulto.
**Escrito sem .NET e sem TensorFlow no ambiente: nada foi compilado nem testado.** Rode `dotnet build -c Release` e mande os erros.

## Como funciona
- `App/Services/ImageGuard.cs`: a cada ~2 s, com a opção ligada, **bloqueio ativo ou proteções permanentes**, fora de uma trava e
  com o foco fora do próprio SharkBlocker, captura cada monitor e pede a pontuação.
- `App/Services/NsfwClassifier.cs`: carrega `nsfw\model.onnx` (ao lado do .exe) com o ONNX Runtime, reduz a imagem para 224x224
  e devolve a soma das classes "adulto" (`model.json`: hentai + porn + sexy).
- Decisão: **qualquer** pontuação acima do limite minimiza as janelas (Win+M) e trava a tela, em **escada**: 1ª vez **5 min**, 2ª **15 min**,
  da 3ª em diante **30 min** (`StateManager.StartImageScreenLock`; contagem em `BlockState.ImageStrikes`, persiste ao reabrir o app e zera
  após 24 h sem detecções). **Carência:** nos 45 s depois de uma trava acabar, só minimiza (sem nova trava). Não olha a área de trabalho nem a
  barra de tarefas. Limites: Cautelosa 0,90, Padrão 0,80, Sensível 0,65 (`BlockState.ImageSensitivity`, hoje só Padrão na interface).
- Opção em Configurações › "Detector de imagens". **Ligar não pede senha; desligar passa por `UninstallFlow.TryAuthorizeRemoval`.**
- Nada é salvo nem enviado. O log guarda só a pontuação (`Imagem: pontuação 0,83 (limite 0,80)`).

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
3. `dotnet build -c Release` (ou o `quick-install.ps1`), abra o app e confira "Modelo: Modelo carregado e pronto.".
4. **Teste só com imagens neutras ou limítrofes** (praia, esporte) para calibrar; não precisa de conteúdo explícito.
O conversor lê o grafo congelado direto (Conv, BatchNorm, Relu6, Add, AvgPool, MatMul, Softmax) e foi conferido contra um cálculo
independente em numpy (diferença máxima ~2e-6 em 4 imagens de teste). Não foi comparado com o TFLite do Android nem rodou no .NET.

## Limites honestos
- Janelas com proteção contra captura (alguns players/DRM) saem pretas: o detector não vê nada nelas.
- A imagem pode ficar visível 1 a 3 s antes da trava.
- Modelos treinados em fotos erram em imagens de IA e ilustração/anime; há falsos positivos (30 min de trava).
- Não fecha a janela: só minimiza tudo (Win+M). Se a imagem continuar aberta depois da carência, pode travar de novo.
- Consumo: uma captura + uma inferência por monitor a cada ~2 s.
- **O Watchdog NÃO compila `ImageGuard.cs`/`NsfwClassifier.cs`** (usam WinForms/WPF e ONNX): não os inclua em `Watchdog.csproj`.
- O instalador (`Installer.iss`) copia a pasta `publish` do App; a pasta `nsfw\` vai junto, e o ONNX Runtime nativo também.
