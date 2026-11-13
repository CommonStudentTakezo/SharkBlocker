# quick-install.ps1
#
# Alternativa rapida ao Inno Setup, pensada pra testes: publica os dois projetos e instala tudo
# numa pasta de destino ja na estrutura que o app espera (App\ e Watchdog\ lado a lado), depois
# abre o app elevado.
#
# Uso (a partir da pasta raiz do projeto, a que tem o SharkBlocker.sln):
#   .\quick-install.ps1
#   .\quick-install.ps1 -Destination "C:\SharkBlockerTeste"
#   .\quick-install.ps1 -SkipPublish   # reusa o publish ja existente, so copia
#   .\quick-install.ps1 -RecoverOnly   # so verifica/conclui uma instalacao interrompida
#
# COMO FUNCIONA (instalacao transacional):
#   1. Um DIARIO (ProgramData\SharkBlocker\install-journal.json) registra cada etapa:
#      Started > Paused > Staged > BackedUp > Copying > Copied > Validated > Complete.
#   2. Os arquivos novos vao primeiro para uma pasta de PREPARO (<Destino>.staging) e sao validados
#      ali (existencia, tamanho, SHA-256, JSON valido). A instalacao so e tocada depois disso.
#   3. Antes de copiar, o que ja existe e salvo em BACKUP (<Destino>.backup). Se a copia ou a
#      validacao final falhar, o backup e restaurado (rollback).
#   4. As protecoes (servico, tarefa de logon) sao SEMPRE retomadas no bloco finally.
#   5. Se o script for interrompido (Ctrl+C, queda de energia, janela fechada), uma tarefa agendada
#      (SharkBlockerInstallRecovery, SYSTEM, no boot) e a proxima execucao deste script fazem a
#      VERIFICACAO DE INTEGRIDADE: concluem a copia a partir do preparo se ele estiver integro,
#      senao restauram o backup; em ambos os casos retomam as protecoes.
#   6. Ao final, uma validacao pos-instalacao compara a instalacao com a origem e confere o servico.
#
# OBS: este arquivo usa apenas caracteres ASCII de proposito (sem acentos, sem travessao).
# O PowerShell 5.1 padrao do Windows le arquivos .ps1 sem BOM usando a codificacao ANSI do sistema,
# o que corrompe qualquer acento.

param(
    [string]$Destination = "$env:LOCALAPPDATA\SharkBlockerTest",
    [switch]$SkipPublish,
    [switch]$RecoverOnly   # so executa a verificacao de integridade/recuperacao e sai
)

# ---- Auto-elevacao ----
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Este script precisa ser admin (pra parar o servico antes de sobrescrever os arquivos)." -ForegroundColor Yellow
    Write-Host "Reabrindo elevado..." -ForegroundColor Yellow
    $argList = @('-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"")
    if ($Destination) { $argList += @('-Destination', "`"$Destination`"") }
    if ($SkipPublish) { $argList += '-SkipPublish' }
    if ($RecoverOnly) { $argList += '-RecoverOnly' }
    Start-Process PowerShell -Verb RunAs -ArgumentList $argList
    exit
}

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$Destination = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\')
$ServiceName = "SharkBlockerWatchdog"
$AppTaskName = "SharkBlockerApp"
$RecoveryTaskName = "SharkBlockerInstallRecovery"
$Components = @('App', 'Watchdog', 'BrowserExtension', 'ExtensionSigningKey')

$dataDir = Join-Path $env:ProgramData 'SharkBlocker'
$journalFile = Join-Path $dataDir 'install-journal.json'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# ---- Log ----
# Normal: install-log.txt na Area de Trabalho. Recuperacao no boot (conta SYSTEM, sem Area de
# Trabalho): install-recovery-log.txt em ProgramData\SharkBlocker.
if ($RecoverOnly) { $logFile = Join-Path $dataDir 'install-recovery-log.txt' }
else { $logFile = Join-Path ([Environment]::GetFolderPath('Desktop')) 'install-log.txt' }
try { Start-Transcript -Path $logFile -Force | Out-Null } catch { }

function Write-Step($msg) { Write-Host ">> $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "AVISO: $msg" -ForegroundColor Yellow }

# Roda um comando nativo sem que o stderr dele vire excecao (PowerShell 5.1 com $ErrorActionPreference
# = Stop transforma qualquer linha de stderr em erro terminante). Devolve o codigo de saida.
function Invoke-Native([scriptblock]$Block) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = 0
        & $Block 2>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $old }
}

function Get-List($x) { if ($null -eq $x) { return @() } return @($x | Where-Object { $_ }) }

# ---------------------------------------------------------------------------------------------
# Diario (journal): escrita atomica (arquivo temporario + Move-Item) para nunca ficar truncado.
# ---------------------------------------------------------------------------------------------
function Save-Journal($j) {
    $j.updatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    $tmp = "$journalFile.tmp"
    $j | ConvertTo-Json -Depth 6 | Set-Content -Path $tmp -Encoding UTF8
    Move-Item -Path $tmp -Destination $journalFile -Force
}

function Read-Journal {
    if (-not (Test-Path -LiteralPath $journalFile)) { return $null }
    try { return (Get-Content -LiteralPath $journalFile -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch {
        Write-Warn "Diario ilegivel ($($_.Exception.Message)). Sera ignorado."
        return $null
    }
}

function Set-Stage($j, [string]$stage) {
    $j.stage = $stage
    Save-Journal $j
    Write-Host "   [etapa: $stage]" -ForegroundColor DarkGray
}

function New-Journal {
    return @{
        version      = 1
        stage        = 'Started'
        destination  = $Destination
        staging      = "$Destination.staging"
        backup       = "$Destination.backup"
        paused       = $false
        stagingValid = $false
        components   = @()
        backed       = @()
        fresh        = @()
        startedUtc   = (Get-Date).ToUniversalTime().ToString('o')
        updatedUtc   = (Get-Date).ToUniversalTime().ToString('o')
    }
}

# ---------------------------------------------------------------------------------------------
# Copia e validacao
# ---------------------------------------------------------------------------------------------
function Invoke-Robo([string]$src, [string]$dst, [switch]$Mirror) {
    # robocopy: codigos 0-7 = sucesso (com ou sem arquivos copiados); 8 ou mais = falha.
    if ($Mirror) { $a = @($src, $dst, '/MIR') } else { $a = @($src, $dst, '/E') }
    $a += @('/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    $code = Invoke-Native { & robocopy.exe @a }
    if ($code -ge 8) { throw "robocopy falhou (codigo $code): $src -> $dst" }
}

# Compara a pasta $dir com a pasta de referencia $srcDir. Devolve a lista de problemas (vazia = ok).
function Test-Tree([string]$dir, [string]$srcDir) {
    $problems = @()
    if (-not (Test-Path -LiteralPath $dir)) { return @("pasta ausente: $dir") }
    $srcFull = (Resolve-Path -LiteralPath $srcDir).Path.TrimEnd('\')
    foreach ($f in (Get-ChildItem -LiteralPath $srcDir -Recurse -File -Force)) {
        $rel = $f.FullName.Substring($srcFull.Length).TrimStart('\')
        $t = Join-Path $dir $rel
        if (-not (Test-Path -LiteralPath $t)) { $problems += "ausente: $rel"; continue }
        $ti = Get-Item -LiteralPath $t -Force
        if ($ti.Length -ne $f.Length) { $problems += "tamanho diferente: $rel"; continue }
        $h1 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
        $h2 = (Get-FileHash -LiteralPath $t -Algorithm SHA256).Hash
        if ($h1 -ne $h2) { $problems += "conteudo diferente (SHA-256): $rel"; continue }
        if ($f.Name -eq 'manifest.json' -or $f.Name -like '*.runtimeconfig.json' -or $f.Name -like '*.deps.json') {
            try { Get-Content -LiteralPath $t -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null }
            catch { $problems += "JSON invalido: $rel" }
        }
    }
    return $problems
}

# Arquivos sem os quais a instalacao nao funciona.
function Test-Mandatory([string]$base, [bool]$hasExtension) {
    $problems = @()
    $need = @('App\SharkBlocker.exe', 'Watchdog\Watchdog.exe')
    if ($hasExtension) { $need += 'BrowserExtension\manifest.json' }
    foreach ($n in $need) {
        $p = Join-Path $base $n
        if (-not (Test-Path -LiteralPath $p)) { $problems += "arquivo essencial ausente: $n" }
        elseif ((Get-Item -LiteralPath $p).Length -eq 0) { $problems += "arquivo essencial vazio: $n" }
    }
    return $problems
}

function Copy-StagingToDestination($j) {
    foreach ($c in (Get-List $j.components)) {
        $s = Join-Path $j.staging $c
        $d = Join-Path $j.destination $c
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        Invoke-Robo $s $d
    }
}

# Valida a instalacao ($j.destination) contra o preparo ($j.staging). Devolve problemas.
function Test-InstalledAgainstStaging($j) {
    $problems = @()
    foreach ($c in (Get-List $j.components)) {
        $problems += @(Test-Tree (Join-Path $j.destination $c) (Join-Path $j.staging $c))
    }
    $problems += @(Test-Mandatory $j.destination (@(Get-List $j.components) -contains 'BrowserExtension'))
    return $problems
}

function Restore-Backup($j) {
    Write-Step "Desfazendo (rollback) a partir do backup..."
    foreach ($c in (Get-List $j.backed)) {
        $b = Join-Path $j.backup $c
        if (Test-Path -LiteralPath $b) { Invoke-Robo $b (Join-Path $j.destination $c) -Mirror }
    }
    foreach ($c in (Get-List $j.fresh)) {
        $d = Join-Path $j.destination $c
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    }
}

function Remove-Leftovers($j) {
    foreach ($p in @($j.staging, $j.backup)) {
        if ($p -and (Test-Path -LiteralPath $p)) {
            try { Remove-Item -LiteralPath $p -Recurse -Force } catch { Write-Warn "Nao consegui apagar $p : $($_.Exception.Message)" }
        }
    }
}

# ---------------------------------------------------------------------------------------------
# Protecoes (servico + tarefa de logon): pausar e SEMPRE retomar
# ---------------------------------------------------------------------------------------------
# Marca "reinicio planejado" (valido por poucos minutos): o Watchdog, o Guard e o app reabrem o app
# normalmente, mas NAO travam a tela por causa do encerramento feito por esta instalacao.
function Set-PlannedRestart {
    try { Set-Content -Path (Join-Path $dataDir 'planned-restart.flag') -Value ([DateTime]::UtcNow.Ticks) -Encoding ASCII }
    catch { Write-Warn "Nao consegui gravar o marcador de reinicio planejado: $($_.Exception.Message)" }
}

# Reabre o app da instalacao que ficou valendo. A pausa encerra o app; se a instalacao falha ou e
# interrompida (Ctrl+C), retomar so o servico/tarefa nao basta: sem protecoes permanentes ligadas o
# Watchdog nao o reabre. Nao faz nada se ja ha um app rodando, se o exe nao existe ou se isto roda
# como SYSTEM (tarefa de recuperacao no boot: la nao existe sessao de usuario para abrir janela).
function Restart-App($j) {
    try {
        if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { return }
        $dest = if ($j -and $j.destination) { [string]$j.destination } else { $Destination }
        $exe = Join-Path $dest "App\SharkBlocker.exe"
        if (-not (Test-Path -LiteralPath $exe)) { return }
        if (Get-Process -Name "SharkBlocker" -ErrorAction SilentlyContinue) { return }
        Set-PlannedRestart
        Write-Step "Reabrindo o app da instalacao anterior..."
        Start-Process -FilePath $exe
    }
    catch { Write-Warn "Nao consegui reabrir o app: $($_.Exception.Message)" }
}

function Resume-Protections {
    Set-PlannedRestart
    Write-Step "Retomando as protecoes (servico e tarefa de logon)..."
    $null = Invoke-Native { & sc.exe config $ServiceName start= auto }
    $null = Invoke-Native { & schtasks.exe /Change /TN $AppTaskName /ENABLE }
    $null = Invoke-Native { & sc.exe start $ServiceName }

    # Confere se de fato voltou (o servico pode nao existir ainda numa primeira instalacao: o app o cria).
    $qc = & sc.exe qc $ServiceName 2>$null
    if ($LASTEXITCODE -eq 0) {
        if (-not ($qc -match 'AUTO_START')) { Write-Warn "O servico $ServiceName nao ficou em inicio automatico. Rode: sc.exe config $ServiceName start= auto" }
    }
    else {
        Write-Host "   (servico ainda nao existe: o app o instala ao abrir)" -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------------------------
# Tarefa de recuperacao no boot
# ---------------------------------------------------------------------------------------------
function Register-RecoveryTask {
    $tr = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"' + $PSCommandPath + '\" -RecoverOnly'
    $code = Invoke-Native { & schtasks.exe /Create /TN $RecoveryTaskName /SC ONSTART /RU SYSTEM /RL HIGHEST /TR $tr /F }
    if ($code -ne 0) { Write-Warn "Nao consegui registrar a tarefa de recuperacao no boot (codigo $code). A recuperacao ocorrera na proxima execucao deste script." }
}

function Unregister-RecoveryTask {
    $null = Invoke-Native { & schtasks.exe /Delete /TN $RecoveryTaskName /F }
}

# ---------------------------------------------------------------------------------------------
# Verificacao de integridade / recuperacao de uma instalacao interrompida
# ---------------------------------------------------------------------------------------------
function Invoke-Recovery($j) {
    Write-Step "Instalacao anterior INTERROMPIDA (etapa: $($j.stage)). Verificando integridade..."
    $resolvedAs = $null

    try {
        switch ($j.stage) {
            { $_ -in @('Copying', 'Copied') } {
                # A instalacao foi tocada: conclui a partir do preparo se ele estiver integro; senao, rollback.
                $canRollForward = $j.stagingValid -and (Test-Path -LiteralPath $j.staging)
                if ($canRollForward) {
                    Write-Step "Preparo integro encontrado: concluindo a copia pendente..."
                    try {
                        Copy-StagingToDestination $j
                        $problems = @(Test-InstalledAgainstStaging $j)
                        if ($problems.Count -eq 0) { $resolvedAs = 'Complete' }
                        else { Write-Warn ("A conclusao nao passou na validacao: " + ($problems -join '; ')) }
                    }
                    catch { Write-Warn "Falha ao concluir a copia: $($_.Exception.Message)" }
                }
                if (-not $resolvedAs) {
                    Restore-Backup $j
                    $resolvedAs = 'RolledBack'
                }
            }
            'Validated' { $resolvedAs = 'Complete' }   # arquivos ja validados; so faltava retomar/finalizar
            default     { $resolvedAs = 'RolledBack' } # a instalacao nem chegou a ser tocada
        }
    }
    catch {
        Write-Host "ERRO na recuperacao: $($_.Exception.Message)" -ForegroundColor Red
        Write-Warn "O diario foi mantido: a recuperacao sera tentada de novo no proximo boot ou na proxima execucao."
        # Mesmo assim nunca deixa as protecoes desligadas.
        Resume-Protections
        Restart-App $j
        return $false
    }

    Resume-Protections
    Restart-App $j
    $j.paused = $false
    Set-Stage $j $resolvedAs
    Remove-Leftovers $j
    Unregister-RecoveryTask
    Write-Host "Recuperacao concluida: $resolvedAs." -ForegroundColor Green
    return $true
}

# ---------------------------------------------------------------------------------------------
# Execucao
# ---------------------------------------------------------------------------------------------
$exitCode = 0
$mutex = New-Object System.Threading.Mutex($false, 'Global\SharkBlockerQuickInstall')
$haveMutex = $false

try {
    try { $haveMutex = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveMutex = $true }
    if (-not $haveMutex) {
        if ($RecoverOnly) { Write-Host "Outra instalacao esta em andamento. Nada a recuperar agora."; exit 0 }
        throw "Ja existe outra execucao do quick-install em andamento."
    }

    # --- Verificacao de integridade: toda execucao (e a tarefa do boot) comeca por aqui ---
    $pending = Read-Journal
    if ($pending -and ($pending.stage -notin @('Complete', 'RolledBack'))) {
        $ok = Invoke-Recovery $pending
        if (-not $ok) { throw "A recuperacao da instalacao anterior nao terminou. Veja o log: $logFile" }
    }
    elseif ($RecoverOnly) {
        Write-Host "Nenhuma instalacao pendente. Integridade OK." -ForegroundColor Green
        Unregister-RecoveryTask
    }
    if ($RecoverOnly) { exit 0 }

    $pauseScript = Join-Path $PSScriptRoot "protection-pause.ps1"
    if (-not (Test-Path $pauseScript)) { throw "Nao achei $pauseScript (precisa estar na mesma pasta deste script)." }

    # Aviso util: se o servico roda de OUTRA pasta, copiar para $Destination nao atualiza o Watchdog
    # que esta valendo (o que roda continua sendo o antigo).
    $svcExe = $null
    try {
        $qc = & sc.exe qc $ServiceName 2>$null
        foreach ($line in $qc) { if ($line -match 'BINARY_PATH_NAME\s*:\s*"?([^"]+?)"?\s*$') { $svcExe = $Matches[1].Trim() } }
    } catch { }
    $expectedWatchdog = [System.IO.Path]::GetFullPath((Join-Path $Destination "Watchdog\Watchdog.exe"))
    if ($svcExe -and -not [string]::Equals($svcExe, $expectedWatchdog, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "ATENCAO: o servico roda de:      $svcExe" -ForegroundColor Yellow
        Write-Host "         voce esta copiando para: $expectedWatchdog" -ForegroundColor Yellow
        Write-Host "         Para atualizar o que esta valendo, use -Destination com a raiz da instalacao real." -ForegroundColor Yellow
    }

    # --- Abre a transacao ---
    $j = New-Journal
    Save-Journal $j
    Register-RecoveryTask
    $succeeded = $false

    try {
        # 1) Pausa as protecoes. O diario ja marca "paused" ANTES, para a recuperacao saber retomar
        #    mesmo se a interrupcao acontecer no meio da pausa.
        Write-Step "Pausando as protecoes (servico, app, protecao de pasta)..."
        $j.paused = $true
        Set-Stage $j 'Paused'
        $pauseArgs = @{ Action = "Pause"; InstallRoot = $Destination; SkipPassword = $true }
        & $pauseScript @pauseArgs
        if ($LASTEXITCODE -eq 1) { throw "Cancelado pelo usuario." }

        # 2) Publica (a pausa vem antes porque o build pode sobrescrever um Watchdog.exe em uso)
        if (-not $SkipPublish) {
            Write-Step "Publicando App (self-contained win-x64)..."
            & dotnet publish (Join-Path $root "App") -c Release -r win-x64 --self-contained true
            if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar App" }

            Write-Step "Publicando Watchdog (self-contained win-x64)..."
            & dotnet publish (Join-Path $root "Watchdog") -c Release -r win-x64 --self-contained true
            if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar Watchdog" }
        }

        $appPublish = Join-Path $root "App\bin\Release\net8.0-windows\win-x64\publish"
        $watchdogPublish = Join-Path $root "Watchdog\bin\Release\net8.0-windows\win-x64\publish"
        if (-not (Test-Path $appPublish)) { throw "Nao achei $appPublish. Rode sem -SkipPublish primeiro." }
        if (-not (Test-Path $watchdogPublish)) { throw "Nao achei $watchdogPublish. Rode sem -SkipPublish primeiro." }

        $sources = @{
            'App'                = $appPublish
            'Watchdog'           = $watchdogPublish
            'BrowserExtension'   = (Join-Path $root "BrowserExtension")
            'ExtensionSigningKey' = (Join-Path $root "ExtensionSigningKey")
        }
        $staged = @()
        foreach ($c in $Components) { if (Test-Path -LiteralPath $sources[$c]) { $staged += $c } }
        $j.components = $staged

        # 3) Preparo (staging): copia tudo para uma pasta a parte e valida ali.
        #    ATENCAO: copia-se o CONTEUDO de cada pasta (robocopy origem destino), nunca a pasta
        #    dentro da pasta - isso criaria BrowserExtension\BrowserExtension e deixaria a antiga.
        Write-Step "Preparando os arquivos em $($j.staging) ..."
        if (Test-Path -LiteralPath $j.staging) { Remove-Item -LiteralPath $j.staging -Recurse -Force }
        Set-Stage $j 'Staging'
        foreach ($c in $staged) {
            New-Item -ItemType Directory -Force -Path (Join-Path $j.staging $c) | Out-Null
            Invoke-Robo $sources[$c] (Join-Path $j.staging $c)
        }
        $problems = @()
        foreach ($c in $staged) { $problems += @(Test-Tree (Join-Path $j.staging $c) $sources[$c]) }
        $problems += @(Test-Mandatory $j.staging ($staged -contains 'BrowserExtension'))
        if ($problems.Count -gt 0) { throw ("Os arquivos preparados nao passaram na validacao:`n  " + ($problems -join "`n  ")) }
        $j.stagingValid = $true
        Set-Stage $j 'Staged'

        # 4) Backup do que ja esta instalado (para o rollback)
        Write-Step "Salvando backup da instalacao atual em $($j.backup) ..."
        if (Test-Path -LiteralPath $j.backup) { Remove-Item -LiteralPath $j.backup -Recurse -Force }
        $backed = @(); $fresh = @()
        foreach ($c in $staged) {
            $d = Join-Path $Destination $c
            if (Test-Path -LiteralPath $d) {
                New-Item -ItemType Directory -Force -Path (Join-Path $j.backup $c) | Out-Null
                Invoke-Robo $d (Join-Path $j.backup $c)
                $backed += $c
            }
            else { $fresh += $c }
        }
        $j.backed = $backed
        $j.fresh = $fresh
        Set-Stage $j 'BackedUp'

        # 5) Copia para a instalacao
        Write-Step "Copiando pra $Destination ..."
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        Set-Stage $j 'Copying'
        Copy-StagingToDestination $j
        Set-Stage $j 'Copied'

        # 6) Validacao dos arquivos instalados (ainda com as protecoes pausadas: se falhar, faz rollback)
        Write-Step "Validando os arquivos instalados..."
        $problems = @(Test-InstalledAgainstStaging $j)
        if ($problems.Count -gt 0) { throw ("A instalacao nao passou na validacao:`n  " + ($problems -join "`n  ")) }
        Set-Stage $j 'Validated'
        $succeeded = $true
    }
    catch {
        $exitCode = 1
        Write-Host ""
        Write-Host "ERRO: $($_.Exception.Message)" -ForegroundColor Red
        if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
        if ($j.stage -in @('Copying', 'Copied')) {
            try {
                Restore-Backup $j
                $j.stage = 'RolledBack'
                Write-Host "Rollback concluido: a instalacao anterior foi restaurada." -ForegroundColor Yellow
            }
            catch {
                Write-Host "ERRO no rollback: $($_.Exception.Message)" -ForegroundColor Red
                Write-Warn "O diario foi mantido. A recuperacao sera tentada no proximo boot ou na proxima execucao."
            }
        }
        else { $j.stage = 'RolledBack' }   # a instalacao nem foi tocada
    }
    finally {
        # Roda SEMPRE (erro, Ctrl+C, rollback): nunca deixa o servico desabilitado por engano.
        # Se a execucao foi interrompida NO MEIO da copia (etapa Copying/Copied ainda sem desfecho),
        # resolve primeiro (conclui a partir do preparo ou restaura o backup) para nao religar o
        # servico com arquivos pela metade. Invoke-Recovery ja retoma as protecoes no fim.
        $resumedByRecovery = $false
        if ($j.stage -in @('Copying', 'Copied')) {
            try { $null = Invoke-Recovery $j; $resumedByRecovery = $true }
            catch { Write-Warn "Falha na resolucao da copia interrompida: $($_.Exception.Message)" }
        }
        if (-not $resumedByRecovery) {
            try { Resume-Protections } catch { Write-Warn "Falha ao retomar as protecoes: $($_.Exception.Message)" }
        }
        # Falha ou Ctrl+C: a pausa tinha encerrado o app, entao reabre o da instalacao que ficou valendo.
        # (No sucesso, o app NOVO e aberto mais abaixo.)
        if (-not $succeeded) { Restart-App $j }
        $j.paused = $false
        try { Save-Journal $j } catch { }
    }

    if (-not $succeeded) {
        if ($j.stage -eq 'RolledBack') { Remove-Leftovers $j; Unregister-RecoveryTask }
        Write-Host "Instalacao NAO concluida. Log em: $logFile" -ForegroundColor Red
        exit 1
    }

    # --- Finaliza a transacao ---
    Set-Stage $j 'Complete'
    Remove-Leftovers $j
    Unregister-RecoveryTask

    Write-Step "Pronto. Estrutura final:"
    Write-Host "  $Destination\App\SharkBlocker.exe"
    Write-Host "  $Destination\Watchdog\Watchdog.exe"
    Write-Host ""
    Set-PlannedRestart
    Write-Step "Abrindo o app (ja estamos elevados, sem precisar de -Verb RunAs de novo)..."
    # (se o app ja foi aberto pela tarefa de logon, o mutex de instancia unica evita duplicar)
    Start-Process (Join-Path "$Destination\App" "SharkBlocker.exe")

    # --- Validacao pos-instalacao do servico (o app instala/aponta o servico ao abrir) ---
    Write-Step "Conferindo se o servico aponta para a instalacao nova (ate 45 s)..."
    $serviceOk = $false
    for ($i = 0; $i -lt 15 -and -not $serviceOk; $i++) {
        Start-Sleep -Seconds 3
        $qc = & sc.exe qc $ServiceName 2>$null
        $cur = $null
        foreach ($line in $qc) { if ($line -match 'BINARY_PATH_NAME\s*:\s*"?([^"]+?)"?\s*$') { $cur = $Matches[1].Trim() } }
        if ($cur -and [string]::Equals($cur, $expectedWatchdog, [System.StringComparison]::OrdinalIgnoreCase)) { $serviceOk = $true }
    }
    if ($serviceOk) { Write-Host "   Servico OK: $expectedWatchdog" -ForegroundColor Green }
    else { Write-Warn "O servico ainda nao aponta para $expectedWatchdog. Abra o SharkBlocker.exe como Administrador e confira com: sc.exe qc $ServiceName" }

    Write-Host ""
    Write-Host "Concluido. Log em: $logFile" -ForegroundColor Green
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "ERRO: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    Write-Host "Log completo em: $logFile" -ForegroundColor Yellow
}
finally {
    if ($haveMutex) { try { $mutex.ReleaseMutex() } catch { } }
    $mutex.Dispose()
    try { Stop-Transcript | Out-Null } catch { }
}
exit $exitCode
