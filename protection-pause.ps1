# protection-pause.ps1
#
# Pausa (e depois retoma) tudo que impede de substituir o Watchdog.exe e o resto da instalacao:
#
#   1. Recuperacao automatica do servico ("sc failure ... restart/5000"): matar o processo
#      faz o Windows reinicia-lo em 5 s.            -> o servico e DESABILITADO enquanto pausado.
#   2. O app, ao abrir, recria e INICIA o servico.   -> o app e encerrado.
#   3. O Watchdog reabre o app a cada ~20 s quando ha protecoes permanentes ligadas.
#                                                    -> o servico e parado ANTES do app.
#   4. A tarefa agendada que abre o app no logon.    -> desabilitada enquanto pausado.
#   5. A pasta de instalacao tem a permissao de excluir NEGADA (FolderProtection).
#                                                    -> a negacao e removida; o Watchdog a
#                                                       reaplica em poucos segundos depois.
#
# Uso (PowerShell; ele pede elevacao sozinho):
#   .\protection-pause.ps1 -Action Pause    # desliga tudo, espera o Watchdog.exe ficar livre
#   ... copie/compile/instale o que quiser ...
#   .\protection-pause.ps1 -Action Resume   # religa tudo
#
# Opcoes:
#   -InstallRoot "C:\caminho\da\instalacao"   pasta que tem App\, Watchdog\ e BrowserExtension\
#                                             (se omitido, usa a pasta do servico registrado)
#
# PORTAO (pause-gate.ps1): para PAUSAR, o script
#   - RECUSA se houver bloqueio temporizado ou trava de tela ativos (nem com senha);
#   - fora disso, pede a SENHA do SharkBlocker (3 tentativas). Nao existe mais -Force nem
#     "digite ATUALIZAR" (so numa primeira instalacao, sem senha definida).
# Chamado pelo quick-install.ps1 (-SkipPassword), nao pede a senha, mas a recusa por bloqueio continua.
# "Resume" nao precisa de senha: religar protecoes nunca e um risco.
#
# IMPORTANTE: enquanto pausado, NADA esta protegido. Rode "Resume" logo depois. Se o script for
# interrompido no meio, rode "-Action Resume" (ou: sc.exe config SharkBlockerWatchdog start= auto).
#
# Limite honesto: o portao fecha o caminho facil, nao a vontade de um Administrador determinado
# (sc.exe, icacls, regedit e modo de seguranca continuam existindo).
#
# OBS: so caracteres ASCII, de proposito (PowerShell 5.1 le .ps1 sem BOM como ANSI).

param(
    [ValidateSet("Pause", "Resume")]
    [string]$Action = "Pause",
    [string]$InstallRoot = "",
    [switch]$SkipPassword   # so para o quick-install.ps1: nao pede a senha (a recusa por bloqueio ativo continua)
)

$ServiceName = "SharkBlockerWatchdog"
$TaskName = "SharkBlockerApp"

# ---- Auto-elevacao ----
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Precisa de Administrador. Reabrindo elevado..." -ForegroundColor Yellow
    $argList = @('-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"", '-Action', $Action)
    if ($InstallRoot) { $argList += @('-InstallRoot', "`"$InstallRoot`"") }
    if ($SkipPassword) { $argList += '-SkipPassword' }
    Start-Process PowerShell -Verb RunAs -ArgumentList $argList
    exit
}

$ErrorActionPreference = "Continue"

function Write-Step($msg) { Write-Host ">> $msg" -ForegroundColor Cyan }

# Marca "reinicio planejado" por alguns minutos (ver AppPresence.IsPlannedRestart no app).
function Set-PlannedRestart {
    try {
        $dir = Join-Path $env:ProgramData "SharkBlocker"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Set-Content -Path (Join-Path $dir "planned-restart.flag") -Value ([DateTime]::UtcNow.Ticks) -Encoding ASCII
    } catch { Write-Host "Aviso: nao consegui gravar o marcador de reinicio planejado: $($_.Exception.Message)" -ForegroundColor Yellow }
}

# Caminho do Watchdog.exe registrado no servico (ou $null se o servico nao existe).
function Get-ServiceExePath {
    $out = & sc.exe qc $ServiceName 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    foreach ($line in $out) {
        if ($line -match 'BINARY_PATH_NAME\s*:\s*"?([^"]+?)"?\s*$') { return $Matches[1].Trim() }
    }
    return $null
}

function Get-ServiceStatusText {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) { return "NaoExiste" }
    return [string]$svc.Status
}

function Remove-FolderDeny($folder) {
    if (-not $folder -or -not (Test-Path $folder)) { return }
    Write-Host "   Removendo a negacao de exclusao em: $folder"
    & icacls.exe "$folder" /remove:d "*S-1-5-32-544" /T /C /Q 2>&1 | Out-Null
    & icacls.exe "$folder" /remove:d "*S-1-5-32-545" /T /C /Q 2>&1 | Out-Null
}

# Espera ate conseguir abrir o arquivo com acesso exclusivo (= ninguem mais o esta usando).
function Wait-FileFree($path, $seconds) {
    if (-not $path -or -not (Test-Path $path)) { return $true }
    for ($i = 0; $i -lt $seconds; $i++) {
        try {
            $fs = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
            $fs.Close()
            return $true
        } catch {
            Start-Sleep -Seconds 1
        }
    }
    return $false
}

function Stop-ByName($name) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            Write-Host "   Encerrando $name (PID $($_.Id))..."
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
            $null = $_.WaitForExit(10000)
        } catch {
            Write-Host "   Nao consegui encerrar $name (PID $($_.Id)): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

# ============================================================================
if ($Action -eq "Pause") {

    $exePath = Get-ServiceExePath
    if ($exePath) {
        Write-Host "Servico registrado aponta para: $exePath"
        if (-not $InstallRoot) {
            # ...\Watchdog\Watchdog.exe -> raiz = duas pastas acima
            $InstallRoot = Split-Path (Split-Path $exePath -Parent) -Parent
        }
    } else {
        Write-Host "Servico $ServiceName ainda nao existe (primeira instalacao?). Seguindo mesmo assim."
    }

    # PORTAO: recusa durante bloqueio/trava e exige a senha. Nada abaixo roda sem passar por aqui.
    Write-Host ""
    Write-Host "ISTO DESLIGA AS PROTECOES (servico, app, protecao de pasta) ate voce rodar -Action Resume." -ForegroundColor Yellow
    $gate = Join-Path $PSScriptRoot "pause-gate.ps1"
    if (-not (Test-Path -LiteralPath $gate)) { Write-Host "RECUSADO: nao achei pause-gate.ps1 ao lado deste script." -ForegroundColor Red; exit 1 }
    . $gate
    if (-not (Test-PauseAllowed -SkipPassword:$SkipPassword)) { exit 1 }

    # Reinicio PLANEJADO: avisa Watchdog/app/Guard que o encerramento a seguir e de proposito
    # (nao travar a tela). So chega aqui depois do portao (bloqueio livre + senha).
    Set-PlannedRestart

    Write-Step "1/6 Impedindo que o servico seja reiniciado (desabilitando)..."
    & sc.exe config $ServiceName start= disabled 2>&1 | Out-Null

    Write-Step "2/6 Desabilitando a tarefa que abre o app no logon..."
    & schtasks.exe /Change /TN $TaskName /DISABLE 2>&1 | Out-Null

    Write-Step "3/6 Parando o servico (o Watchdog) e esperando ele terminar..."
    # Auditoria U4: o servico nega "Parar" a Administradores; aqui (depois do portao de senha) volta ao padrao.
    & sc.exe sdset $ServiceName 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' 2>&1 | Out-Null
    & sc.exe stop $ServiceName 2>&1 | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        $status = Get-ServiceStatusText
        if ($status -eq "Stopped" -or $status -eq "NaoExiste") { break }
        Start-Sleep -Seconds 1
    }
    Write-Host "   Estado do servico: $(Get-ServiceStatusText)"
    # Se ainda sobrou processo (orfao, ou servico que nao obedeceu): como o servico esta
    # desabilitado, matar nao dispara reinicio.
    Stop-ByName "Watchdog"

    Write-Step "4/6 Encerrando o app (so agora, com o Watchdog parado, para ele nao ser reaberto)..."
    Stop-ByName "SharkBlocker"
    Stop-ByName "SharkBlocker"   # nome antigo (versoes anteriores)
    # Encerramento de proposito: apaga o batimento do app, senao o Watchdog/o app novo tratariam
    # isto como fechamento forcado e travariam a tela por 30 min.
    Remove-Item -Path (Join-Path $env:ProgramData "SharkBlocker\app-alive.flag") -Force -ErrorAction SilentlyContinue
    Remove-Item -Path (Join-Path $env:ProgramData "SharkBlocker\watchdog-running.flag") -Force -ErrorAction SilentlyContinue

    Write-Step "5/6 Removendo a protecao de pasta (negacao de exclusao)..."
    Remove-FolderDeny $InstallRoot
    if ($exePath) { Remove-FolderDeny (Split-Path (Split-Path $exePath -Parent) -Parent) }

    Write-Step "6/6 Esperando o Watchdog.exe ficar livre..."
    $targets = @()
    if ($exePath) { $targets += $exePath }
    if ($InstallRoot) { $targets += (Join-Path $InstallRoot "Watchdog\Watchdog.exe") }
    $allFree = $true
    foreach ($t in ($targets | Select-Object -Unique)) {
        if (Wait-FileFree $t 20) {
            Write-Host "   Livre: $t" -ForegroundColor Green
        } else {
            Write-Host "   AINDA EM USO: $t  (feche qualquer coisa que o esteja usando: antivirus, Explorer, VS)" -ForegroundColor Red
            $allFree = $false
        }
    }

    Write-Host ""
    if ($allFree) {
        Write-Host "PAUSADO. Pode substituir os arquivos. Depois rode: .\protection-pause.ps1 -Action Resume" -ForegroundColor Green
        exit 0
    } else {
        Write-Host "PAUSADO, mas algum arquivo continua em uso. Rode -Action Resume se quiser desfazer." -ForegroundColor Yellow
        exit 2
    }
}

# ============================================================================
if ($Action -eq "Resume") {
    Set-PlannedRestart   # o servico/app que vao subir agora nao devem tratar a pausa como fechamento forcado
    Write-Step "Religando o servico (inicio automatico) e a tarefa de logon..."
    & sc.exe config $ServiceName start= auto 2>&1 | Out-Null
    & schtasks.exe /Change /TN $TaskName /ENABLE 2>&1 | Out-Null

    $status = Get-ServiceStatusText
    if ($status -eq "NaoExiste") {
        Write-Host "O servico ainda nao existe. Abra o SharkBlocker.exe (como Administrador): ele o instala." -ForegroundColor Yellow
    } else {
        & sc.exe start $ServiceName 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        Write-Host "   Estado do servico: $(Get-ServiceStatusText)"
        Write-Host "   A protecao de pasta volta sozinha em ate ~5 s (o Watchdog a reaplica)."
    }
    Write-Host "RETOMADO." -ForegroundColor Green
    exit 0
}
