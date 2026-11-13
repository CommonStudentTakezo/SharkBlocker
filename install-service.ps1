# install-service.ps1
#
# Registra o Watchdog como SERVICO NATIVO do Windows (inicio automatico, conta LocalSystem, reinicio
# automatico em 1 s) e abre o app uma vez para ele registrar a tarefa de abertura no logon.
#
# POR QUE O SERVICO E O Watchdog.exe E NAO O SharkBlocker.exe:
#   O SharkBlocker.exe e um aplicativo WPF (janela, bandeja, trava de tela). Servicos do Windows rodam
#   na Sessao 0, isolada, SEM acesso a area de trabalho do usuario - uma janela nunca apareceria. O
#   desenho correto e o que o projeto usa: um servico sem tela (Watchdog.exe) que fica vivo desde o boot
#   e reabre a interface (SharkBlocker.exe) na sessao do usuario quando ela for fechada.
#
# POR QUE LocalSystem: o servico precisa (a) editar o arquivo hosts e as politicas dos navegadores,
# (b) proteger a pasta de instalacao e (c) abrir o app na sessao do usuario (WTSQueryUserToken exige
# SYSTEM/SeTcbPrivilege). Uma conta de menos privilegio (LocalService, conta virtual) nao consegue (c).
# O servico NAO escuta na rede: so um named pipe local e o servidor HTTP em 127.0.0.1.
#
# Uso (PowerShell; pede elevacao sozinho):
#   .\install-service.ps1 -InstallRoot "C:\SharkBlocker"      # pasta que tem App\ e Watchdog\
#   .\install-service.ps1                                       # reaplica a config do servico ja instalado
#   .\install-service.ps1 -InstallRoot "C:\SharkBlocker" -NoLaunch
#
# E idempotente: pode rodar de novo para reparar a configuracao.
# Para REMOVER, use uninstall-service.ps1 (passa pela SENHA do app).
#
# OBS: so caracteres ASCII, de proposito (PowerShell 5.1 le .ps1 sem BOM como ANSI).

param(
    [string]$InstallRoot = "",
    [switch]$NoLaunch
)

$ServiceName = "SharkBlockerWatchdog"

# ---- Auto-elevacao ----
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Precisa de Administrador. Reabrindo elevado..." -ForegroundColor Yellow
    $argList = @('-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"")
    if ($InstallRoot) { $argList += @('-InstallRoot', "`"$InstallRoot`"") }
    if ($NoLaunch) { $argList += '-NoLaunch' }
    Start-Process PowerShell -Verb RunAs -ArgumentList $argList
    exit
}

$ErrorActionPreference = "Continue"
function Write-Step($m) { Write-Host ">> $m" -ForegroundColor Cyan }

# Roda sc.exe com a linha de comando EXATA (o PowerShell 5.1 trata aspas embutidas de forma ambigua
# quando se passam argumentos separados) e devolve o codigo de saida.
function Invoke-Sc([string]$Arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "sc.exe"
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $null = $proc.StandardOutput.ReadToEnd()
    $null = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    return $proc.ExitCode
}

# Extrai o caminho do .exe do PathName do servico (com ou sem aspas, com ou sem argumentos).
function Get-ExePath([string]$pathName) {
    if ($pathName -match '^\s*"([^"]+)"') { return $Matches[1] }
    return $pathName.Trim()
}

function Get-ServiceInfo {
    try { return Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop } catch { return $null }
}

# ---- Descobre a raiz da instalacao ----
$existing = Get-ServiceInfo
if (-not $InstallRoot) {
    if ($existing -and $existing.PathName) {
        $exe = Get-ExePath $existing.PathName
        $InstallRoot = Split-Path (Split-Path $exe -Parent) -Parent
        Write-Host "Usando a raiz do servico ja instalado: $InstallRoot"
    } else {
        Write-Host "ERRO: informe -InstallRoot (pasta que tem App\ e Watchdog\)." -ForegroundColor Red
        exit 2
    }
}
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$watchdogExe = Join-Path $InstallRoot "Watchdog\Watchdog.exe"
$appExe = Join-Path $InstallRoot "App\SharkBlocker.exe"

foreach ($f in @($watchdogExe, $appExe)) {
    if (-not (Test-Path -LiteralPath $f)) {
        Write-Host "ERRO: nao achei $f. Rode o quick-install.ps1 antes (ele publica e copia os arquivos)." -ForegroundColor Red
        exit 2
    }
}

# ---- Cria ou corrige o servico ----
$failed = $false
if (-not $existing) {
    Write-Step "Criando o servico $ServiceName ..."
    $code = Invoke-Sc ('create {0} binPath= "{1}" start= auto obj= LocalSystem DisplayName= "SharkBlocker Watchdog"' -f $ServiceName, $watchdogExe)
    if ($code -ne 0) { Write-Host "ERRO: sc create falhou (codigo $code)." -ForegroundColor Red; exit 1 }
} else {
    Write-Step "Servico ja existe: reaplicando a configuracao..."
    $null = Invoke-Sc ('sdset {0} "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)"' -f $ServiceName)   # auditoria U4: volta ao descritor padrao (o servico nega "Parar" a Administradores)
    $null = Invoke-Sc "stop $ServiceName"   # para trocar o binario de forma limpa (ignora se ja estava parado)
    Start-Sleep -Seconds 2
    $code = Invoke-Sc ('config {0} binPath= "{1}" start= auto obj= LocalSystem DisplayName= "SharkBlocker Watchdog"' -f $ServiceName, $watchdogExe)
    if ($code -ne 0) { Write-Host "ERRO: sc config falhou (codigo $code)." -ForegroundColor Red; $failed = $true }
}

$null = Invoke-Sc ('description {0} "Mantem o SharkBlocker protegido: reabre o app e aplica o bloqueio em segundo plano."' -f $ServiceName)

# Recuperacao: reinicia em 1 s nas 3 primeiras falhas (e continua repetindo a ultima acao), zera o contador apos 1 dia.
$null = Invoke-Sc "failure $ServiceName reset= 86400 actions= restart/1000/restart/1000/restart/1000"
$null = Invoke-Sc "failureflag $ServiceName 1"

# Modo seguro: por padrao o Windows NAO carrega servicos de terceiros no modo seguro. O valor padrao
# "Service" nas chaves SafeBoot\Minimal e SafeBoot\Network faz o Watchdog subir tambem la.
# Idempotente (so grava se faltar ou diferir). O proprio Watchdog reafirma as mesmas chaves a cada ~60 s,
# e a desinstalacao (SharkBlocker.exe --uninstall, com senha) as remove. A pausa de protecoes NAO as apaga.
function Ensure-SafeBootKey([string]$Mode) {
    $sbKey = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$Mode\$ServiceName"
    $cur = $null
    try { $cur = (Get-Item -LiteralPath $sbKey -ErrorAction Stop).GetValue('') } catch { }
    if ($cur -ceq 'Service') { return $true }
    try {
        if (-not (Test-Path -LiteralPath $sbKey)) { $null = New-Item -Path $sbKey -Force }
        Set-Item -LiteralPath $sbKey -Value 'Service'
        return $true
    } catch {
        Write-Host "AVISO: nao consegui gravar $sbKey ($($_.Exception.Message))" -ForegroundColor Yellow
        return $false
    }
}
$null = Ensure-SafeBootKey 'Minimal'
$null = Ensure-SafeBootKey 'Network'

Write-Step "Iniciando o servico..."
$null = Invoke-Sc "start $ServiceName"
Start-Sleep -Seconds 3

# ---- Verificacao ----
Write-Step "Conferindo..."
$svc = Get-ServiceInfo
if (-not $svc) { Write-Host "FALHA: o servico nao existe depois da instalacao." -ForegroundColor Red; exit 1 }

function Check($ok, $msg) {
    if ($ok) { Write-Host "  [OK]   $msg" -ForegroundColor Green }
    else { Write-Host "  [FALHA] $msg" -ForegroundColor Red; $script:failed = $true }
}
$pathOk = $svc.PathName -and ((Get-ExePath $svc.PathName) -ieq $watchdogExe)
Check ($svc.StartMode -eq 'Auto') "inicio automatico (StartMode = $($svc.StartMode))"
Check ($svc.StartName -eq 'LocalSystem') "conta LocalSystem (StartName = $($svc.StartName))"
Check $pathOk "binario = $watchdogExe"
Check ($svc.State -eq 'Running') "em execucao (State = $($svc.State))"
$qf = (& sc.exe qfailure $ServiceName 2>&1 | Out-String)
Check ($qf -match '(RESTART|REINICIAR).*1000') "reinicio automatico em 1 s configurado"
foreach ($mode in @('Minimal', 'Network')) {
    $sbVal = $null
    try { $sbVal = (Get-Item -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName" -ErrorAction Stop).GetValue('') } catch { }
    Check ($sbVal -ceq 'Service') "registrado para o modo seguro (SafeBoot\$mode)"
}

if ($failed) {
    Write-Host ""
    Write-Host "A instalacao do servico teve problemas. Veja C:\ProgramData\SharkBlocker\app.log e rode health-check.ps1." -ForegroundColor Red
    exit 1
}

if (-not $NoLaunch) {
    Write-Step "Abrindo o app (ele registra a tarefa de abertura no logon e a tarefa de revivificacao do servico)..."
    Start-Process $appExe
}
Write-Host ""
Write-Host "Servico $ServiceName instalado e ativo. Confira a qualquer momento com: .\health-check.ps1" -ForegroundColor Green
exit 0
