# HANDOFF — SharkBlocker (PC) e SharkBlocker Android

Documento de passagem de contexto para **outro modelo do Claude** (ex.: Sonnet 5.5, Opus, Haiku) continuar o trabalho caso a conversa
anterior tenha sido encerrada. Escrito em 2026-10-29 e atualizado em 2026-11-03. Leia-o inteiro **uma vez**; depois use
os `AGENTS.md` de cada projeto como mapa. Nada aqui contém senhas ou chaves (e nenhum modelo deve pedir ou imprimir).

Versão do documento: 5 (2026-11-03). **Leia primeiro `INSTRUCOES-PROXIMO-AGENTE.md`** (estado atual e primeiros passos de cada projeto). Android: quarentena, primeira revisão e urgência **confirmadas em uso**; detector sem modelo. PC: filtros de rede (DNS local + firewall) **confirmados**: bloqueiam tudo durante a trava · Projetos: **SharkBlocker (PC, Windows)** e **SharkBlocker Android** · Idioma de tudo: **português (BR)**.

---

## 0. Como usar este documento (leia primeiro)

1. Leia as seções 1 (usuário), 2 (regras de conduta) e 3 (visão geral). Depois a do projeto em que vai mexer (4 = PC, 5 = Android).
2. **Peça ao usuário os arquivos atuais** antes de editar qualquer coisa (seção 11): você NÃO tem os fontes na memória. Os zips que o
   usuário baixou das conversas anteriores podem estar desatualizados em relação ao que ele aplicou.
3. Confira o que existe com `ls`/`grep` antes de supor. Este documento descreve o estado em 2026-10-29; o disco do usuário pode ter evoluído.
4. Entregue sempre arquivos prontos (zip/patch) com **instruções de onde extrair** (seção 6).
5. Se este documento e o código divergirem, **o código vence**; corrija o documento no fim da tarefa.

## 1. Quem é o usuário e como conversar

- **Professor de química do ensino médio**, escola pequena. Escreve em português do Brasil. Não é programador profissional, mas compila e
  testa por conta própria (Windows + Android Studio) e devolve mensagens de erro e prints.
- **Quer:** entrega pronta (arquivos), passo a passo curto e correto, honestidade sobre o que foi ou não testado, e a "Ellen" (mascote) com
  personalidade. Já pediu: tema rosa/vermelho/cinza/preto, interface fluida e polida, Ellen **tsundere** com muitas falas, sorteio melhor, rostos sem contorno falho.
- **Estilo de resposta que funcionou:** direto, caloroso, sem enrolação; começa pelo que importa; listas só quando ajudam; uma pergunta no máximo por resposta;
  avisa **claramente** quando algo não foi compilado/testado ("não compilei nem testei no celular").
- **Autoriza implementar** quando pede ("Pode começar a implementar", "Continuar"). Se estiver em dúvida entre duas leituras, escolha a mais útil, diga a suposição em uma linha e faça.
- **Contexto de uso:** autocontrole pessoal (bloquear conteúdo adulto, proxies/VPNs, distrações). Trate com respeito e **sem julgamento**. O app é uma ferramenta; a Ellen provoca,
  mas **nunca humilha** (ver 2.6).
- **Ambiente dele:** PC Windows com Android Studio (JDK 17), `adb` em `%LOCALAPPDATA%\Android\Sdk\platform-tools` (não está no PATH por padrão), PowerShell 5.1.
  Celular de teste: **Redmi 23129RA5FL, Android 15 (HyperOS)**, instalado em modo teste, com Device Owner funcionando.
- **Seu ambiente de trabalho (o do assistente):** sandbox Linux **sem .NET, sem Kotlin/Gradle e sem Android SDK**; tem `node`, `python3` (com PIL, numpy, scipy), `java` 21, `zip/unzip`.
  Rede desligada. Ou seja: **você não consegue compilar nenhum dos dois apps**. O que dá para rodar de verdade: os testes Node da extensão e da paridade (seção 8).

## 2. Regras de conduta (inegociáveis)

1. **Honestidade sobre testes.** Nunca diga que compilou ou testou o que não rodou. Diga o que foi verificado (ex.: "`node tests/parity/run-parity.js`: 128 casos OK") e o que não foi.
2. **Nunca peça, imprima ou grave a senha do usuário**, nem a chave `ExtensionSigningKey/extension_key.pem` (privada; nunca abrir/colar).
3. **Nada que enfraqueça a proteção sem autorização completa** no código: toda ação que afrouxa passa por senha + trava de tela de 15 min (+ confirmações nas graves). Não crie atalhos nem "modo fácil".
4. **Fail-closed:** sem senha definida, ações críticas são recusadas. Não afrouxe.
5. **Privacidade:** relatórios de diagnóstico não contêm sites, palavras, apps escolhidos, senha ou texto digitado; só contagens e estados. Logs guardam o **motivo** do bloqueio, não o conteúdo. Mantenha assim.
6. **Ellen:** tsundere (marra por fora, cuida por dentro). Provoca e disfarça o carinho ("Não é como se eu estivesse preocupada…"), **nunca** ofende, envergonha ou culpa pelo vício/impulso; o apoio real fica por baixo. Frases **neutras de gênero** em relação ao usuário. Em diálogos com informação importante, a fala vem **antes** e a informação factual continua completa **depois**.
   Se o usuário parecer em sofrimento sério, saia da persona e ofereça ajuda humana/profissional com gentileza.
7. **Idioma:** comentários, logs, UI e docs em português (BR).
8. **Mudança mínima e cirúrgica.** Os dois projetos têm armadilhas caras (ver 4.4 e 5.5). Leia o trecho, não o arquivo inteiro.
9. **Entregas parciais só com aviso.** Patches precisam dizer quais arquivos substituem; se o usuário puder se perder, entregue o projeto completo.

## 3. Visão geral

| | **SharkBlocker (PC)** | **SharkBlocker Android** |
|---|---|---|
| Plataforma | Windows 10/11, .NET 8 (WPF) + serviço Watchdog (LocalSystem) + extensão MV3 | Android 10+ (API 29), Kotlin, **sem bibliotecas externas**, Device Owner |
| Nome antigo | "ColdTurkeyClone" (inspirado no Cold Turkey Blocker) | — |
| Núcleo de proteção | `hosts` + políticas de navegador + extensão + bloqueio de processos + Watchdog | VPN só de DNS + políticas do Chrome/Edge + suspensão de apps + acessibilidade + quiosque |
| Autorização | senha PBKDF2 + trava de **30 min** + confirmações (desde 2026-10-29) | **igual** (porte fiel), também **30 min** |
| Estado | `state.json` (ProgramData) + Registro, HMAC | arquivo assinado (HMAC no Keystore) + backup |
| Origem das listas | `BrowserExtension/adult-terms.js`, `proxy-terms.js` | **os mesmos dois arquivos**, em `app/src/main/assets/terms/` |
| Testes que rodam no ambiente do assistente | `node tests/extension/run-tests.js` (464 OK), `node tests/parity/run-parity.js` (197 OK) | nenhum (só `ParityTest.kt`/`PortTests.kt`, que o usuário roda no Android Studio; o `ParityTest.kt` foi recriado em 2026-11-03, pois tinha sumido do zip) |

Os dois apps compartilham a mesma **lógica de avaliação de URL/busca**, existente em **três versões**: JS (`background.js`, `matcher.js`, `deobf.js`, a referência),
C# (`UrlPolicy.cs`, `Deobfuscator.cs`) e Kotlin (`UrlPolicy.kt`, `TermMatcher.kt`, `Deobfuscator.kt`). Mudou uma, mude as três e rode a paridade (seção 8).

## 4. SharkBlocker PC

### 4.1 Estrutura
`App/` (SharkBlocker.exe, WPF + WinForms só para o ícone; exige admin) · `Watchdog/` (serviço LocalSystem sem tela) · `BrowserExtension/` (MV3, versão 1.10) ·
scripts na raiz (`quick-install.ps1`, `install-service.ps1`, `uninstall-service.ps1`, `health-check.ps1`, `protection-pause.ps1`, `pause-gate.ps1`, `diagnostico.ps1`) ·
`tests/extension` (Node) · `tests/parity` (casos compartilhados) · `tests/SharkBlocker.ParityTests` (xUnit) · `docs/AUDITORIA-CONTORNO.md` · `ARCHITECTURE.md` · `AGENTS.md`.

**Leia `AGENTS.md` do PC primeiro** (mapa tarefa → arquivo, armadilhas, ritmos). Não leia o `README.md` inteiro (~52 KB, histórico repetido): use `grep -n "^## " README.md`.

### 4.2 O que já está implementado (resumo)
- **2026-10-29 (PC):** trava de **30 min** (`StateManager.ScreenLockPenalty`), Ellen **muito tsundere** (muitas falas novas; expressão `shy`), **19 rostos corrigidos** + `ellen-angry.png` sem halo, sorteio em sacola (`ShuffleBag`). **Não compilado** (sem .NET aqui): peça ao usuário `dotnet build` e `health-check.ps1`.
- Bloqueio por `hosts` (+ subdomínios, SafeSearch, DoH por nome), políticas de Chrome/Edge/Brave, extensão instalada à força, bloqueio de processos/VPNs/proxies, VPN nativa (`rasdial`).
- Watchdog (serviço) ⇄ Guard (no app): reabrem um ao outro; app fechado à força → trava 15 min; modo seguro (U1); instalador transacional.
- Senha fail-closed, tentativas com espera, trava de tela com a Ellen, desinstalação com confirmações.
- Bloqueios **agendados** recorrentes (ex.: 22h–6h) com trava no horário; **sequência/estatísticas**; falas da Ellen por evento; widget flexível (arrastar, multi-monitor).
- **Auditoria de contorno U1–U8: todas implementadas** (U8 só documentada).
- Novos nesta rodada: `diagnostico.ps1` (relatório sem dados sensíveis), `tests/parity` (casos compartilhados + runner Node), `SharkBlocker.ParityTests` (xUnit), `docs/GUIA-ENDURECIMENTO.md`.

### 4.3 Verificado × não verificado
- Verificado (Node): extensão (464 casos) e paridade JS (197 casos).
- **Nunca validado pelo assistente:** nada em C# (sem .NET aqui) nem em Windows real. O usuário compilou e usa o PC app. **Confirmado por ele em 2026-11-03:** os filtros de rede do PC bloqueiam tudo durante uma trava. Rodadas posteriores a 2026-11-01 (exceções do `nsfw`, `EvaluateTyped`, endurecimento do S3 de 2026-11-03) não têm confirmação de compilação: peça o `dotnet build` e `dotnet test`.
- O projeto de teste `SharkBlocker.ParityTests` **não foi compilado**: pode precisar de ajuste (referência a um exe self-contained exige `ValidateExecutableReferencesMatchSelfContained=false`, já posto).

### 4.4 Armadilhas do PC (resumo do AGENTS.md; leia o original)
Watchdog compila arquivos do App (`Compile Include` no `Watchdog.csproj`); espelhos C#⇄JS; named pipe/mutex com DACL via P/Invoke; senha fail-closed;
estado em dois lugares com HMAC; não chamar `icacls` à toa; não usar `Process.MainModule`; não listar todos os processos em laço de 1 s; **nunca** pôr
`SharkBlocker`/`Watchdog`/`explorer`/`svchost`/`lsass` na lista bloqueável; `System.IO` não está nos usings implícitos do App; fechamento normal só via `AppPresence.MarkCleanExit`.
`.ps1` do projeto são **ASCII** de propósito (PowerShell 5.1 lê sem BOM como ANSI).

## 5. SharkBlocker Android

### 5.1 Stack e build
Kotlin 1.9.22 · AGP 8.2.2 · Gradle 8.5 · `compileSdk 34` / `targetSdk 34` / `minSdk 29` · **zero dependências** (só plataforma) · pacote `com.sharkblocker.android` · UI feita em código (sem XML de layout).
`gradle.properties`: `modoTeste=true` (APK `testOnly`, removível pelo adb) ou `false` (final). Chave de assinatura: debug por padrão; `keystore.properties` opcional (ver guia).

### 5.2 Mapa do código (`app/src/main/java/com/sharkblocker/android/`)
| Arquivo | Responsabilidade |
|---|---|
| `Core.kt` | `C` (constantes), `Store` (pasta protegida do dispositivo), `Logger`, `Crypto` (PBKDF2, HMAC no Keystore), `TrustedClock`, `Tamper` (sinalizadores por arquivo), `Mode` (modo teste/carência) |
| `State.kt` | `BlockState`, `Schedule`, `StateStore` (JSON assinado + backup, cadeado) |
| `Auth.kt` | `PasswordGate`, `AuthFlow` (senha → trava 15 min → janela de 10 min), `Locker` (trava de tela/bloqueio manual) |
| `Scheduler.kt` | agendas (porte de `BlockScheduler.cs`) |
| `Stats.kt` | sequência, estatísticas, marcos |
| `Policy.kt` | tudo do Device Owner: restrições, VPN sempre ativa, DNS privado, quiosque, políticas do Chrome/Edge, remoção; `VpnKick` |
| `AppBlocker.kt` | suspensão de apps/navegadores/VPNs; lista do que **nunca** tocar |
| `GuardService.kt` / `WatchdogService.kt` / `Receivers.kt` | ciclo de 5 s; 2º vigia em outro processo (`:watchdog`); boot, alarme de 60 s, job de 15 min |
| `DnsVpnService.kt` + `HostPolicy.kt` | filtro de DNS local (só roteia o IP do "DNS falso" 10.77.0.1); NXDOMAIN, SafeSearch por IP, encaminha ao Cloudflare Família |
| `UrlPolicy.kt`, `TermMatcher.kt`, `Deobfuscator.kt`, `TermLists.kt` | portes de `background.js`/`matcher.js`/`deobf.js` + leitura dos `.js` de termos |
| `GuardAccessibilityService.kt` | lê barra de endereço/busca, devolve ao início apps bloqueados e telas sensíveis dos Ajustes |
| `LockActivity.kt` | trava de tela em *lock task* (quiosque), com botões de emergência e a Ellen |
| `Ellen.kt` | `EllenTexts` (pools de falas), `EllenFaces` (19 rostos por humor), `Shuffle` (sorteio em sacola), `EllenChatter` (notificações) |
| `MainActivity.kt`, `Ui.kt` | telas (Início, Sites, Apps, Agenda, Proteção) e kit de UI |
| `Health.kt`, `Diagnostics.kt` | cartão de saúde; relatório de diagnóstico |
| `ImageGuard.kt`, `NsfwClassifier.kt` | detector de imagens: quando olhar/o que fazer; classificador TFLite por reflexão (sem modelo no repositório) |
Docs: `AGENTS.md`, `README.md`, `docs/GUIA-INSTALACAO.md`, `docs/GUIA-ENDURECIMENTO.md`. Scripts: `scripts/instalar-no-celular.ps1`, `scripts/remover-modo-teste.ps1`.

### 5.3 Decisões de projeto (e por quê)
- **Device Owner** é a base: sem ele o app é contornável em segundos. Instalação por `adb shell dpm set-device-owner` (exige **sem contas** e **sem usuários extras**).
- **VPN só de DNS** (estilo DNS66): roteia só `10.77.0.1/32`; `setAlwaysOnVpnPackage(..., lockdown=false)` de propósito — lockdown cortaria a internet porque não há encaminhamento de pacotes. Limite assumido: apps com DNS próprio/DoH embutido escapam; por alguns segundos sem VPN o tráfego passa sem filtro.
- **Chrome/Edge** por política gerenciada (`URLBlocklist`, `ForceGoogleSafeSearch`, `DnsOverHttpsMode=off`, sem anônimo); demais navegadores **suspensos**.
- **Tempo:** `TrustedClock` (relógio monotônico dentro do boot; hora automática imposta pelo DO).
- **Detecção de adulteração é relaxada** em modo teste, sem senha e por 3 min após instalar/atualizar (só registra no log) — criada depois de uma trava falsa logo na abertura. O 2º vigia decide só pelo **batimento** (`heartbeat.txt`), não por `getRunningServices`.
- **Rostos da Ellen:** 19 figurinhas quadradas (360 px), contorno rosa contínuo (as de contorno escuro/translúcido receberam contorno rosa). Não usar os corpos inteiros (cortados nas bordas).
- **Sorteio:** `Shuffle.next` (sacola: só repete após esgotar, nunca igual duas vezes seguidas); `EllenFaces.moodOf(texto)` escolhe o humor pelo texto.
- **Quarentena de apps novos (60 dias) e bloqueio de jogos** (2026-10-29): resposta aos apps de IA que se passam por jogos de Mahjong/"Remove the Screw" e liberam imagens adultas com classificação livre. Apps de usuário com `firstInstallTime` < 60 dias ficam suspensos; aprovar exige `AuthFlow`. `blockNewInstalls` (opcional) usa `DISALLOW_INSTALL_APPS` e **também bloqueia `adb install`**.
- **Botão de urgência:** trava de 30 min sem senha (só reforça), com respiração guiada; no Início, no aviso fixo e no atalho do ícone. Motivo `urgency` não zera a sequência.
- **Detector de imagens:** captura de tela pela acessibilidade (API 30), classificador **opcional** no aparelho, **sem modelo no repositório** (não foi possível baixar/treinar daqui). Depende de o usuário escolher um modelo (`docs/DETECTOR-DE-IMAGENS.md`). Não testado.
- **Primeira revisão da quarentena** (2026-11-01, **confirmada em uso**): apps antigos (instalados antes de `quarantineSince`) podem ser liberados de uma vez, uma única vez e sem senha; tudo que chega depois exige `AuthFlow`. Ver `AGENTS.md`.
- **Trava de 30 min** nos dois: Android `C.LOCK_MS`; PC `StateManager.ScreenLockPenalty` (antes havia o literal 15 espalhado; o Watchdog não enxerga `ScreenLock`, por isso a constante mora no `StateManager`). O preset "15 min" de bloqueio manual do PC é só um atalho de duração.
- **Paleta (a do app de PC):** fundo `#0E0D13`, cartões `#211A28`, rosa `#FF6B8E`, vermelho `#E0264D`, texto `#F3F1F6`, suave `#A79FB5`, aviso `#F5B83D`.

### 5.4 Estado e histórico (o que o usuário fez e confirmou)
Linha do tempo resumida: projeto entregue → usuário compilou no Android Studio e achou o APK em `app\build\intermediates\apk\debug\` → script de instalação corrigido (erro `NativeCommandError` do PowerShell escondia a mensagem do `adb`) →
`set-device-owner` falhou no Redmi ("Calling identity is not authorized"; causa típica: "Depuração USB (Configurações de segurança)" desligada em Xiaomi) → depois funcionou → **a tela de trava apareceu logo ao abrir** → corrigido por patch (carência/modo teste/sem senha + vigia por batimento + chave do Keystore sem corrida) →
o botão Run ▶ do Android Studio passou a dar *"Couldn't terminate previous instance"* (esperado: o app não aceita ser fechado à força) → atualizar com `adb install -r -t <apk>` →
patches aplicados em ordem: **tema** (cores) → **trava** → **interface** (animações) → **ellen** (falas tsundere) → **rostos** (19 rostos, sorteio em sacola, marcos) → **diagnóstico/paridade** → **quarentena/urgência/detector/30 min** (o mais recente, 2026-10-29; o usuário relatou que o app não impedia instalar/abrir apps recentes).
**Confirmado pelo usuário em 2026-11-03:** quarentena, primeira revisão e urgência funcionam no Android. **Não confirmado:** detector (sem modelo), tela apagada no bloqueio agendado, trava de 30 min no aparelho; peça o log (Proteção › Copiar relatório de diagnóstico).

### 5.5 Armadilhas do Android
- **Run ▶ não instala** com o app como Device Owner: use `adb install -r -t`. Com **Proteção máxima** ligada a depuração USB some: a atualização sem adb é o botão Proteção › "Atualizar o app" (backlog #5, ainda não testado em aparelho).
- **Instalar o final:** `modoTeste=false` e, antes, `remover-modo-teste.ps1` (só funciona em `testOnly`). No build final **não há saída por adb**: só o fluxo do app ou *wipe* pelo recovery.
- **Xiaomi/HyperOS:** exige "Depuração USB (Configurações de segurança)" e "Instalar via USB"; apps duplos/segundo espaço contam como usuários extras e impedem o Device Owner. Serviços podem ser mortos por economia de bateria.
- `DISALLOW_APPS_CONTROL` só no Android 10; do 11 em diante `setUserControlDisabledPackages`. `clearDeviceOwnerApp` é obsoleto e pode falhar no 14+ (o fluxo avisa).
- Foreground services do Android 14 exigem tipo (`specialUse`) e permissão (já no manifesto). Direct boot: estado em `createDeviceProtectedStorageContext()` (usar `Store`, nunca `filesDir`).
- A trava depende de `SYSTEM_ALERT_WINDOW` (concedida por `appops` no script). A reativação automática da acessibilidade depende de `WRITE_SECURE_SETTINGS` (concedida por `pm grant`).
- **Kotlin sem compilador aqui:** confira parênteses/chaves com um verificador simples, evite APIs sem `if (SDK_INT >= …)`, e não use `Process`/APIs que exijam bibliotecas novas.
- Strings com `$` em regex Kotlin: escape (`\$`).

## 6. Fluxo de trabalho e de entrega

1. Descubra o arquivo pelo mapa (`AGENTS.md`), leia só o trecho, edite com mudança mínima.
2. Verifique o que for verificável: `node tests/extension/run-tests.js`, `node tests/parity/run-parity.js`; no Kotlin, um verificador de balanceamento de `{}`/`()` por arquivo.
3. **Entrega por patch zip cumulativo**: os arquivos **inteiros** alterados, com o **mesmo caminho relativo** do projeto, para o usuário extrair por cima (substituir). Diga **qual pasta** (a que contém `settings.gradle.kts`/`.idea` no Android; a que contém `SharkBlocker.sln` no PC).
   Se a mudança tocar muita coisa, entregue o zip completo (sem `build/`, `bin/`, `obj/`).
4. Atualize o `AGENTS.md` do projeto (registro curto) e este HANDOFF se algo estrutural mudou.
5. Termine com: o que mudou, como aplicar, o que **não** foi verificado, e (se útil) uma pergunta objetiva.
6. Android: atualização no aparelho = **Build APK(s)** + `adb install -r -t`. PC: `quick-install.ps1`.

## 7. Backlog priorizado (com critérios de aceitação)

Legenda: ✅ feito nesta rodada · ⬜ pendente.

| # | Item | Estado |
|---|---|---|
| 1 | Guia de endurecimento do ambiente | ✅ `docs/GUIA-ENDURECIMENTO.md` |
| 2 | Testes de paridade JS/C#/Kotlin | ✅ casos + runner Node (rodado, OK: 174); testes Kotlin e C# **escritos, não rodados** |
| 3 | Relatório de diagnóstico (Android e PC) | ✅ botão em Proteção; `diagnostico.ps1` |
| 4 | Sequência menos "tudo ou nada" | PC ✅ (2026-11-01) · Android ⬜ |
| 5 | Atualizador seguro no Android | ✅ 2026-11-03 (`UpdateInstaller.kt`; **não compilado nem testado em aparelho**) |
| 6 | Mapa de horários de risco + sugestão de agenda | PC ✅ (2026-11-01) · Android ⬜ |
| 7 | Respiração guiada na tela de trava | ✅ (Android; botão "Respirar comigo" e automática na urgência) |
| 8 | Tela "Compatibilidade do aparelho" (Xiaomi etc.) | ⬜ |
| 9 | PC: filtro de DNS local + firewall contra DoH/DoT | ✅ opção `LocalDnsFilterEnabled`; **confirmado em uso em 2026-11-03: bloqueia tudo durante a trava** |
| 10 | PC: regras de firewall para VPNs/proxies/Tor | ✅ `FirewallBlocker` (confirmado em uso em 2026-11-03) |
| 11 | PC: VMs/WSL/Sandbox como contorno | ✅ opção `BlockVirtualization` (2026-11-01) |
| 12 | PC: instalador assinado + atualização | ⬜ |
| 13 | Android: VPN de túnel completo com lockdown | ⬜ |
| 14 | Android: limite diário por app | ⬜ |
| 15 | Parceiro de responsabilidade (opcional) | ⬜ |
| 16 | Cofre de senha | ⬜ |
| 17 | Diário rápido pós-bloqueio | ⬜ |
| 18 | Listas importáveis (arquivo assinado) | ⬜ |
| 19 | CI (GitHub Actions) nos dois projetos | ✅ 2026-11-04: `.github/workflows/ci.yml` em cada projeto (**não executado**: o usuário não quis criar repositório; no PC há `verificar-tudo.ps1` como alternativa local) |
| 20 | Quarentena de apps novos (60 dias) + bloqueio de jogos (Android) | ✅ **confirmado em aparelho (2026-11-03)**, inclusive a primeira revisão |
| 21 | Botão de urgência (Android) | ✅ **confirmado em aparelho (2026-11-03)** |
| 22 | Detector de imagens (Android): estrutura pronta, **sem modelo** | ✅ parcial; falta o usuário escolher um modelo e calibrar |
| 23 | Alinhar a trava do PC para 30 min | ✅ (2026-10-29; não compilado) |
| 25 | PC: Ellen tsundere + 19 rostos corrigidos + `ShuffleBag` | ✅ (2026-10-29; não compilado) |
| 26 | PC: botão de urgência (como o do Android) | ✅ (confirmado) |
| 27 | Exceção `nsfw` do S3 do modelo travava o PC | ✅ 2026-11-03: extensão **1.12** (a 2ª exceção estava no código mas o `manifest.json` ficara em 1.11, então o Chrome não atualizou); endpoints regionais, `%2F` e `//` aceitos; JS/C#/Kotlin iguais; Android passou a ter as duas exceções |
| 28 | Script `scripts/converter-modelo.py` (`.h5` do GantMan → `.tflite` + `model.json`) | ✅ escrito 2026-11-03, **não testado** (sem TensorFlow no ambiente do assistente) |
| 24 | Detector: motor alternativo (ONNX) ou modelo de detecção por regiões | ⬜ |

### Especificações dos pendentes
- **#4 Sequência:** mostrar "melhor sequência" e "% de dias limpos nos últimos 30". *Onde:* Android `Stats.kt` (guardar `bestStreakDays` e eventos por dia) + `MainActivity.renderHome`; PC `StatsStore.cs` + aba Status. *Aceite:* uma tentativa de burlar zera a **atual**, mas a melhor e a porcentagem permanecem; testes de unidade do cálculo.
- **#5 Atualizador seguro (Android):** botão em Proteção › "Atualizar o app" que, **após `AuthFlow` (nova ação `UPDATE`)**, lê um APK escolhido pelo usuário e instala por `PackageInstaller` (o Device Owner instala sem diálogo). *Riscos:* só aceitar APK com a **mesma assinatura** e `versionCode` maior (o sistema já exige a mesma assinatura); nunca baixar da rede. *Aceite:* atualização com Proteção máxima ligada, sem adb.
- **#6 Mapa de horários de risco:** histograma por hora das tentativas/buscas barradas (`Stats.events`) + sugestão de janela de agenda (botão "Criar agenda sugerida" que abre o editor já preenchido; criar exige a regra normal de agenda). *Aceite:* sem dados suficientes (< 5 eventos) não sugere.
- **#7 Respiração guiada:** na `LockActivity`, círculo que cresce/encolhe 4-7-8, com a Ellen guiando em texto. Opcional (toque para alternar). Sem som obrigatório. *Aceite:* não atrapalha o contador nem a saída automática.
- **#8 Compatibilidade do aparelho:** tela com checklist por fabricante (autoinício, bateria sem restrição, "Depuração USB (segurança)") com atalhos (`Settings` intents) e ✓/✗ quando detectável. *Aceite:* não afirma o que não consegue detectar.
- **#9 PC filtro de DNS local:** proxy DNS no Watchdog reaproveitando as listas/matcher, forçar o DNS do sistema para 127.0.0.1 e bloquear DoH/DoT externos por Windows Filtering Platform/firewall. *Riscos:* alto (rede do usuário); precisa de modo seguro de reversão (`emergency-fix-hosts.bat` análogo). *Aceite:* `health-check.ps1` cobre, desinstalação restaura DNS.
- **#10 Firewall:** regras `netsh advfirewall` para executáveis conhecidos (lista já existente em `ProcessBlocker`), criadas/removidas de forma idempotente (marcador, como em `BrowserPolicyManager`).
- **#11 VM/WSL:** durante o bloqueio, impedir início de `vmware-vmx`, `VirtualBoxVM`, `vmmem`, `WindowsSandbox*`; *Aceite:* nunca mexer em processos do sistema (`NeverKill`).
- **#12 Instalador:** Inno Setup já existe (`Installer.iss`); falta assinatura de código e atualização com manifesto assinado. Custo (certificado) é do usuário.
- **#13 VPN de túnel completo:** encaminhar pacotes (NAT em modo usuário) e filtrar por SNI/QUIC; só então `lockdown=true`. *Risco:* alto (bateria, estabilidade). Fazer atrás de um interruptor, com período longo de teste.
- **#14 Limite diário por app:** `UsageStatsManager` (permissão concedida por `appops`/DO) + suspensão do app ao atingir o limite; reinicia à meia-noite local. Afrouxar o limite exige `AuthFlow`.
- **#15 Parceiro:** **opt-in explícito**, aviso só de tentativas de burlar/desinstalar (nunca o que foi pesquisado), canal configurável. *Aceite:* o usuário vê e pode revogar, e o texto enviado é mostrado antes.
- **#16 Cofre de senha:** gerar senha aleatória exibida uma vez para quem for guardá-la; o app só guarda o hash. Troca/recuperação definida com o usuário (decisão de produto: perguntar).
- **#17 Diário:** nota opcional local após cada bloqueio; nunca sai do aparelho; entra no relatório apenas como **contagem**.
- **#18 Listas importáveis:** arquivo `.json` assinado (HMAC com chave do app ou assinatura Ed25519) para atualizar termos/domínios sem recompilar; validar tamanho e formato; nunca executar nada do arquivo.
- **#19 CI:** GitHub Actions: Android (`./gradlew test assembleDebug`) e PC (`dotnet test`, `node tests/...`). O wrapper (`gradlew`) não existe no repositório Android: gerar com `gradle wrapper --gradle-version 8.5` ou deixar o Android Studio criar.

## 8. Sistema de paridade (JS ⇄ C# ⇄ Kotlin)

- **Fonte da verdade:** a extensão JS. `tests/parity/generate-cases.js` define as entradas com a **intenção** de cada caso e só grava `cases.json` se o JS bater com a intenção. **Não edite `cases.json` à mão.** Lembrete: sempre que mudar `background.js`, **aumente `version` em `BrowserExtension/manifest.json`** (o Chrome só atualiza a extensão se a versão subir; foi a causa do item 27).
- **Rodar:** `node tests/parity/run-parity.js` (JS contra os casos) e `node tests/extension/run-tests.js` (suíte da extensão).
- **Kotlin:** `app/src/test/java/.../ParityTest.kt` lê `app/src/test/resources/parity/cases.json` (cópia do arquivo do PC: **recopie ao regenerar**). **C#:** `tests/SharkBlocker.ParityTests` lê `tests/parity/cases.json` (copiado para a saída pelo `.csproj`); só cobre os casos de URL (`ServiceMatches`/`IsPublicLiteralIp` são `internal`).
- **Exceções fixas de `nsfw`** (nos três): `github.com/gantman/nsfw_model` e `s3.amazonaws.com/ir_public/nsfwjscdn`. Só o "nsfw" literal do trecho é neutralizado; porn/hentai etc., busca e fragmento seguem avaliados. Endereços como `?prefix=nsfwjscdn` e `ir_public.s3.amazonaws.com` seguem barrados de propósito.
- **O que a paridade já achou:** a profundidade de URLs embutidas era 3 no Kotlin e 2 no JS/C#. Corrigido para 2 (`UrlPolicy.MAX_EMBED_DEPTH`).
- **Quando o usuário rodar os testes e algum falhar:** a mensagem lista `rótulo: url esperado=… obtido=…`. Conserte o porte que divergiu; se a regra mudou de propósito, mude JS, C# e Kotlin juntos e regenere.

## 9. Testes manuais do Android (roteiro curto)

Reiniciar 3×; 2 dias sem travas inexplicadas; bloqueio manual de 30 min (app marcado não abre; ligações e alarme funcionam; botão 192 abre o discador); `browser.lol` não abre; busca normal abre; fluxo de autorização (remover um site); desinstalação completa **uma vez**; apps de banco funcionam. Critério para migrar ao build final: seção 6.1 de `docs/GUIA-INSTALACAO.md`.

## 10. Segurança e privacidade deste material

Sem senha, sem chave privada, sem listas pessoais do usuário. O caminho do projeto do usuário pode aparecer em prints; não o replique. Ao ler zips do usuário, **não abra** `ExtensionSigningKey/extension_key.pem`.

## 11. Checklist de retomada (o que pedir ao usuário)

1. Os zips atuais: **PC** (pasta com `SharkBlocker.sln`) e **Android** (pasta com `settings.gradle.kts`), sem `bin/`, `obj/`, `build/`, `.gradle/`, `.idea/`.
2. O **relatório de diagnóstico** (Android: Proteção › Copiar relatório; PC: `.\diagnostico.ps1` como Administrador) e a mensagem de erro, se houver.
3. O que ele quer agora (um item do backlog ou um problema novo) e se **já aplicou** os patches da seção 5.4.
4. Depois de receber: `unzip`, rode `ls`, `grep`, os testes Node, e **só então** edite.

### Prompt sugerido para colar no novo chat
> Você vai continuar um trabalho em dois apps meus (SharkBlocker PC e SharkBlocker Android). Leia o documento HANDOFF-SHARKBLOCKER.md que estou enviando, depois os AGENTS.md dos projetos. Responda em português. Eu vou enviar os zips atuais. Não afirme que compilou ou testou o que não rodou, entregue patches com caminhos e diga onde extrair. Comece me dizendo, em poucas linhas, o que entendeu e o que precisa de mim.

## 12. Glossário
**Device Owner (DO):** app dono do aparelho (poderes de administração total). · **Modo teste:** APK `testOnly` removível por adb. · **Trava de tela/quiosque:** `LockActivity` em *lock task*. · **Carência:** 3 min após instalar/atualizar sem punir. · **Paridade:** mesmos resultados em JS/C#/Kotlin. · **Ellen:** mascote (tsundere). · **U1–U8:** achados da auditoria de contorno do PC. · **Guard/Watchdog:** vigias que se reabrem mutuamente.
