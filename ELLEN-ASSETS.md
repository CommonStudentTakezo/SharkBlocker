# Imagens da Ellen

**Incluídas** (`App/Assets`): 19 figurinhas chibi, recortadas e padronizadas em 400x400 com fundo transparente
(revisadas em 2026-10: contorno rosa contínuo em todas, sem bordas cinzentas nem pontos soltos; as de contorno
escuro/translúcido, que sumiam no fundo escuro, ganharam contorno rosa). Cada expressão tem uma ou mais variações,
e a cada fala o card sorteia uma delas (sem repetir a anterior):

| Expressão | Arquivos |
|---|---|
| calm | ellen-calm.png, -2, -3, -4 |
| determined | ellen-determined.png, -2, -3 (carrancas) |
| sweet | ellen-sweet.png, -2 (com bala e pirulito) |
| warm | ellen-warm.png, -2, -3 |
| smirk | ellen-smirk.png, -2, -3, -4, -5 |
| shy | ellen-shy.png, -2 (envergonhada/chorosa: as falas tsundere) |

Para acrescentar mais: salve como `ellen-{expressão}-{n}.png` (n de 2 a 6) na mesma pasta; o `App.csproj` já
embute `Assets\*.png`. Uma expressão nova ("mood") também precisa entrar em `EllenQuoteService.Moods`.
A imagem `lockscreen-mascot.png` é a reserva se uma expressão ficar sem arquivo. A lista usada pela trava de tela
está em `ScreenLockWindow.OtherImages`.

## Ideias opcionais

1. **`ellen-lock.png`**: a Ellen "de guarda" para a tela de bloqueio, meio corpo chibi, 600x800 px, transparente,
   expressão séria e calma. Hoje a trava usa a cabeça chibi pequena (trocar o nome do arquivo em `LoadMascot`).
2. **`bg-lock-abstract.jpg`**: fundo abstrato escuro para a trava, 1920x1080, degradê grafite (#0E0D13) para vinho
   (#2A0F1D) com escamas de tubarão muito sutis, sem personagem nem texto.
3. **Ícone do app refeito** (`sharkblocker.ico`): tubarão geométrico simples, carmesim sobre grafite, com 16, 24,
   32, 48, 64, 128 e 256 px.

## Observação sobre direitos

A Ellen Joe é personagem da HoYoverse. Para uso pessoal tudo bem; se um dia o SharkBlocker for distribuído
publicamente, vale trocar por uma mascote original (ou obter autorização), porque as imagens e o nome entram no
instalador.

## Tela de trava (cenas)
- `ellen-angry.png` (761x1763, transparente; limpa em 2026-10: sem o brilho claro em volta do cabelo e da touca): SEMPRE a primeira cena da trava, com a Ellen brava.
- A cada 2 min (`Session.SceneSeconds`, em `ScreenLockWindow.cs`) a imagem troca com fade para uma das
  `ellen-*.png` (ordem sorteada a cada trava). Frases bravas: `MotivationalQuotes.AngryQuotes`.
