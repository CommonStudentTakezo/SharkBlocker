# Instruções para o próximo agente de IA — SharkBlocker (Desktop e Android)

Escrito em 2026-10-29 e **atualizado em 2026-11-03**, para quem continuar o trabalho (qualquer modelo do Claude).
Idioma de tudo: **português (BR)**. Este é o **ponto de entrada**: leia-o inteiro, depois `docs/HANDOFF-SHARKBLOCKER.md`
(regras de conduta, perfil do usuário, mapa dos arquivos, backlog detalhado) e o `AGENTS.md` do projeto em que for mexer.

> Estado em 2026-11-03: as funções principais já estão **em uso e confirmadas** pelo usuário (ver cada seção). O que resta está em
> "O que falta" de cada projeto e no HANDOFF, seção 7.

---

## 0. Regras que valem para os dois projetos (resumo; o texto completo está no HANDOFF, seção 2)

1. **Honestidade sobre testes.** O ambiente do agente anterior **não tinha .NET, Kotlin/Gradle nem Android SDK**: **nada de C# e nenhuma mudança posterior do Android foi compilada pelo agente**. Nunca diga que compilou/testou o que não rodou. Diga o que foi verificado e o que não foi.
2. **Nada que enfraqueça a proteção sem autorização completa** (senha + trava de tela + confirmações nas graves). Sem atalhos, sem "modo fácil". Sem senha definida, ações críticas são recusadas (fail-closed).
3. **Nunca peça, imprima nem grave a senha do usuário.** Nunca abra a chave `ExtensionSigningKey/extension_key.pem` (e ela **não está** nos zips).
4. **Privacidade:** relatórios/logs só com estados, contagens e motivos; nunca sites, palavras, apps escolhidos, senha ou texto digitado.
5. **Ellen = tsundere** (marra por fora, cuida por dentro): provoca e disfarça o carinho, **nunca humilha nem culpa**. Frases neutras de gênero em relação ao usuário. A informação importante vem completa **depois** da fala dela. Se o usuário parecer em sofrimento sério, saia da persona e ofereça ajuda humana/profissional.
6. **Mudança mínima e cirúrgica**; os dois projetos têm armadilhas caras (AGENTS.md de cada um).

7. **Extensão do navegador:** sempre que mudar qualquer arquivo de `BrowserExtension/`, **aumente `version` em `manifest.json`** (hoje **1.14**). O Chrome só atualiza se a versão subir; esquecer isso fez a exceção do S3 do modelo não valer no navegador do usuário (2026-11-03).
8. **Nada de arquivos soltos entre projetos:** arquivos Kotlin ficam só no projeto Android e arquivos C# só no do PC (em 2026-11-02 um `UrlPolicy.kt` atualizado foi parar dentro de `SharkBlocker/App/src`, e o Android ficou sem a correção). Entregue zips **sem** `bin/`, `obj/`, `build/`, `.gradle/`, `local.properties`.

### Quem é o usuário (resumo)
Professor de química do ensino médio (escola pequena), escreve em português. Usa Windows (PowerShell 5.1) e Android Studio (JDK 17); compila e devolve erros/prints.
Celular de teste: **Redmi 23129RA5FL, Android 15 (HyperOS)**, instalado em **modo teste** com Device Owner. Autoriza implementar quando pede.
Quer entregas prontas, passo a passo curto e **honestidade**. Gosta da Ellen e do tema rosa/vermelho/cinza/preto.

### Primeira coisa a fazer ao assumir
1. Peça os **zips atuais** que ele tem no disco (podem estar diferentes: ele aplica patches à mão) e, se houver erro, a **mensagem inteira**.
2. Android: **relatório de diagnóstico** (Proteção › "Copiar relatório de diagnóstico"). PC: `.\diagnostico.ps1` como Administrador e o final de `C:\ProgramData\SharkBlocker\app.log`.
3. Rode `ls`/`grep` e os testes Node **antes** de editar.

---

## 1. DESKTOP — SharkBlocker (PC, Windows)

**Pasta do projeto:** a que contém `SharkBlocker.sln`. **Stack:** .NET 8 WPF (`App/`, exige administrador) + serviço Watchdog LocalSystem (`Watchdog/`) + extensão MV3 (`BrowserExtension/`, **versão 1.14**). **Leia primeiro:** `AGENTS.md`. Não leia o `README.md` inteiro (~55 KB): use `grep -n "^## " README.md`.

### Estado (2026-11-03)
- **Confirmado em uso pelo usuário:** build limpo do App até a rodada de 2026-11-01; botão de urgência; **filtros de rede (DNS local + firewall) bloqueiam tudo durante uma trava**.
- **Sem confirmação de compilação/teste** (feito no código, 2026-11-01 a 2026-11-03): sequência melhor (#4), mapa de horários (#6), VMs/WSL (#11), exceções fixas de `nsfw` e `EvaluateTyped`, endurecimento do S3 do modelo (`CanonExceptionHost`, caminho decodificado, `//`). Peça `dotnet build -c Release` e `dotnet test tests/SharkBlocker.ParityTests` e **corrija o que aparecer**.
- **Incidente de 2026-11-03 (resolvido no código, falta o usuário atualizar):** acessar `s3.amazonaws.com/ir_public/nsfwjscdn` travava o PC. Causa mais provável: a 2ª exceção estava em `background.js`, mas `manifest.json` continuou em 1.11, então o Chrome manteve a extensão antiga. Correção: versão **1.12** + endpoints regionais (`s3.us-east-1...`, `s3-us-west-2...`), `%2F` e `//` aceitos. Para o usuário: reinstalar/atualizar o SharkBlocker no PC (`quick-install.ps1`), reiniciar o navegador e conferir **1.12** em `chrome://extensions`. Se ainda travar, pedir a linha `BrowserUrlWatcher: motivo=...` do `app.log` (o App compilado em 2026-11-02 21:40 já tinha as duas exceções).

- **Incidente de DNS (2026-11-09, causa encontrada):** reinstalar/parar o serviço matava o Watchdog no meio da restauração e o DNS ficava em 127.0.0.1 sem ninguém atendendo. Corrigido no código (`Shutdown` com DNS primeiro, espera de 30 s, `RepairIfOrphaned` no kill e na abertura do app), **não compilado**. Peça o teste de reinstalação com o filtro ativo. Detalhes no AGENTS.md.

### Armadilhas do PC (resumo; leia o AGENTS.md)
Watchdog compila arquivos do App (`Compile Include` em `Watchdog.csproj`: **`StateManager`, `LocalStateServer`, `AppPresence`… não podem depender de WPF**); espelhos C# ⇄ JS (`UrlPolicy`/`Deobfuscator` ⇄ `background.js`/`deobf.js`/`matcher.js`); `System.IO` não está nos usings implícitos do App; `.ps1` do projeto são **ASCII**; nunca pôr `SharkBlocker`/`Watchdog`/`explorer`/`svchost`/`lsass` em listas bloqueáveis; fechamento normal só via `AppPresence.MarkCleanExit`.

### Como verificar (sem .NET)
```
node tests/extension/run-tests.js      # 464 ok
node tests/parity/run-parity.js        # 197 ok
node tests/parity/generate-cases.js    # regenera tests/parity/cases.json (só se a regra mudou de propósito; depois recopie para o Android)
```

### O que falta (PC)
#12 instalador assinado + atualização; #15 a #18 (parceiro, cofre de senha, diário, listas importáveis). Detalhes no HANDOFF, seção 7.

---

## 2. ANDROID — SharkBlocker Android

**Pasta do projeto:** a que contém `settings.gradle.kts`. **Stack:** Kotlin 1.9.22, AGP 8.2.2, Gradle 8.5, `compileSdk/targetSdk 34`, `minSdk 29`, pacote `com.sharkblocker.android`, **sem dependências** (única exceção opcional: TensorFlow Lite do detector, desligada por padrão). UI em código (sem XML de layout). **Leia primeiro:** `AGENTS.md`, `docs/GUIA-INSTALACAO.md`, `docs/DETECTOR-DE-IMAGENS.md`. Atualizar o app: `adb install -r -t <apk>` (o botão Run ▶ falha de propósito).

### Estado (2026-11-03)
- **Confirmado pelo usuário no Redmi:** compilação, instalação com Device Owner, trava de tela, **quarentena de apps novos, primeira revisão da quarentena e botão de urgência**.
- **Sem confirmação:** trava de 30 min e tela apagada no bloqueio agendado (`<force-lock />`) em uso real, relatório de diagnóstico com os campos novos, e **tudo o que foi alterado em 2026-11-03** (exceções do S3 em `UrlPolicy.kt`, `PortTests.kt`, `ParityTest.kt`): peça **Build** e `./gradlew test`.
- **Detector de imagens:** estrutura pronta, **sem modelo**. O modelo do GantMan (`.h5`) precisa de conversão: use `scripts/converter-modelo.py` (escrito em 2026-11-03, **não testado**) e siga `docs/DETECTOR-DE-IMAGENS.md` (seção "Obter o modelo sem travar o PC"). Não pesquise "nsfw" no navegador do PC: trava por desenho; só `github.com/GantMan/nsfw_model` e `s3.amazonaws.com/ir_public/nsfwjscdn` têm exceção.
- **Corrigido em 2026-11-03:** o Android estava **sem** as exceções de `nsfw` (a cópia atualizada do `UrlPolicy.kt` estava dentro do projeto do PC) e sem `ParityTest.kt`/`cases.json`; os dois foram restaurados.
- **Atualizador sem adb (#5) implementado em 2026-11-03, não compilado nem testado em aparelho** (`UpdateInstaller.kt`; `versionCode` agora automático). Teste uma atualização real em modo teste antes do build final.
- O release ainda usa a chave de debug: antes do build final crie a **chave própria** (`docs/GUIA-INSTALACAO.md`, seções 6.1 e 7) (o atualizador exige a mesma assinatura que o app instalado). No build final **não há saída por adb**.

### Armadilhas do Android (resumo; leia o AGENTS.md)
Só o processo principal grava o estado (`StateStore`); o `:watchdog` avisa por arquivo (`Tamper.flag`). Tempo sempre por `TrustedClock`. Estado em `Store` (armazenamento protegido do dispositivo), nunca `filesDir`. `setAlwaysOnVpnPackage(..., lockdown=false)` é **de propósito**. `AppBlocker.isProtectedPackage` antes de suspender qualquer app. Detecção de adulteração é **relaxada** em modo teste, sem senha e 3 min após instalar/atualizar (só registra). O job de 15 min (`HeartbeatJob`) é outra coisa e **não** é a trava (que é de 30 min em `C.LOCK_MS`; o PC usa `StateManager.ScreenLockPenalty`, também 30). Strings com `$` em regex Kotlin: escape `\\$`. As listas `adult-terms.js`/`proxy-terms.js` e a lógica de URL são compartilhadas (três versões: JS é a referência).

### O que falta (Android)
Sequência menos "tudo ou nada" (#4) e mapa de horários (#6) no Android, tela de compatibilidade do aparelho (#8), VPN de túnel completo (#13), limite diário por app (#14), modelo do detector (#22/#24), e os itens #15 a #18.

---

## 3. Paridade JS ⇄ C# ⇄ Kotlin
`tests/parity/cases.json` (197 casos) é gerado por `generate-cases.js` (JS é a verdade; **não edite à mão**). Kotlin: `ParityTest.kt` lê `app/src/test/resources/parity/cases.json` (cópia; **recopie ao regenerar**). C#: `tests/SharkBlocker.ParityTests`. Exceções fixas de `nsfw` nos três: `github.com/gantman/nsfw_model` e `s3.amazonaws.com/ir_public/nsfwjscdn`. A paridade já achou 1 divergência real (`MAX_EMBED_DEPTH`).

## 4. Backlog completo
No HANDOFF, seção 7.

## 5. CI
Cada projeto tem `.github/workflows/ci.yml` (2026-11-04, **ainda não executado**: precisa subir ao GitHub; veja o AGENTS.md). Se o primeiro run falhar, conserte o workflow antes de qualquer função nova.
O usuário **não quis criar repositório** (2026-11-04): para o PC use `verificar-tudo.ps1` (roda tudo localmente); no Android, peça `Run tests` no Android Studio (Gradle › verification › `testDebugUnitTest`) e Build.

## 6. Conteúdo dos zips
- PC: projeto **sem** `bin/`, `obj/`, `.vs/` e **sem** `ExtensionSigningKey/extension_key.pem` (chave privada: o usuário guarda a dele; veja `ExtensionSigningKey/LEIA-ME.txt`).
- Android: projeto sem `build/`, `.gradle/`, `.idea/`, `local.properties`. Não contém `gradlew`/`gradle-wrapper.jar` (o Android Studio usa `gradle/wrapper/gradle-wrapper.properties`).
- Ambos têm `docs/HANDOFF-SHARKBLOCKER.md` e este arquivo em `docs/` (**mantenha as duas cópias iguais**).

## 7. Prompt sugerido para o novo chat
> Você vai continuar o trabalho em dois apps meus (SharkBlocker PC e SharkBlocker Android). Leia INSTRUCOES-PROXIMO-AGENTE.md, depois HANDOFF-SHARKBLOCKER.md e o AGENTS.md do projeto em que vamos mexer. Responda em português. Eu vou enviar os zips atuais e a saída do build. Não afirme que compilou ou testou o que não rodou; entregue patches com caminhos e diga em que pasta extrair. Comece me dizendo, em poucas linhas, o que entendeu e o que precisa de mim.
