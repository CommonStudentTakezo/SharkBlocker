# uninstall-service.ps1
#
# Remove o SharkBlocker (servico, tarefas agendadas, regras do hosts, politicas) PELO CAMINHO PROTEGIDO
# DO PROPRIO APP: abre "SharkBlocker.exe --uninstall", que exige a SENHA ADMINISTRATIVA, aplica a trava de
# tela e a sequencia de confirmacoes antes de remover qualquer coisa.
#
# Este script NAO para nem apaga o servico por conta propria. Isso e proposital: um script que removesse
# o servico sem passar pela senha anularia a protecao. (Limite honesto: um Administrador ainda pode usar
# sc.exe diretamente - o modelo se apoia na senha e no atrito, nao em impedir o Administrador do PC.)
#
# Uso:  .\uninstall-service.ps1            (pede elevacao sozinho)
#
# OBS: so caracteres ASCII, de proposito (PowerShell 5.1 le .ps1 sem BOM como ANSI).

$ServiceName = "SharkBlockerWatchdog"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Precisa de Administrador. Reabrindo elevado..." -ForegroundColor Yellow
    Start-Process PowerShell -Verb RunAs -ArgumentList @('-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"")
    exit
}

# Extrai o caminho do .exe do PathName do servico (com ou sem aspas, com ou sem argumentos).
function Get-ExePath([string]$pathName) {
    if ($pathName -match '^\s*"([^"]+)"') { return $Matches[1] }
    return $pathName.Trim()
}

try { $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop } catch { $svc = $null }
if (-not $svc -or -not $svc.PathName) {
    Write-Host "O servico $ServiceName nao esta instalado. Nada a remover." -ForegroundColor Yellow
    exit 0
}

$watchdogExe = Get-ExePath $svc.PathName
$root = Split-Path (Split-Path $watchdogExe -Parent) -Parent
$appExe = Join-Path $root "App\SharkBlocker.exe"
if (-not (Test-Path -LiteralPath $appExe)) {
    Write-Host "ERRO: nao achei $appExe." -ForegroundColor Red
    exit 2
}

Write-Host "Abrindo o desinstalador protegido do SharkBlocker (vai pedir a senha)..." -ForegroundColor Cyan
$p = Start-Process -FilePath $appExe -ArgumentList "--uninstall" -PassThru -Wait
if ($p.ExitCode -eq 0) {
    Write-Host "Desinstalacao autorizada e concluida." -ForegroundColor Green
    Write-Host "Se quiser, apague a pasta de instalacao: $root"
    exit 0
}
Write-Host "Desinstalacao NAO autorizada ou cancelada (codigo $($p.ExitCode)). Nada foi removido." -ForegroundColor Yellow
exit 1
