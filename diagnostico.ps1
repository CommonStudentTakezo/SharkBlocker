# diagnostico.ps1
#
# Gera um relatorio curto do SharkBlocker (PC) para colar numa conversa de suporte e copia para a area de
# transferencia. So LE: nao altera nada. NAO inclui senha, listas de sites/palavras nem conteudo de state.json:
# so versoes, estados, nomes e tamanhos de arquivos, a saida do health-check e as ultimas linhas do app.log
# (com URLs trocadas por <url>).
#
# Uso (PowerShell como Administrador, na pasta do projeto):  .\diagnostico.ps1        (use -SemCopiar para so mostrar)
#
# OBS: so caracteres ASCII, de proposito (PowerShell 5.1 le .ps1 sem BOM como ANSI).

param([switch]$SemCopiar)

$ErrorActionPreference = "Continue"
$ServiceName = "SharkBlockerWatchdog"
$dataDir = Join-Path $env:ProgramData "SharkBlocker"
$lines = New-Object System.Collections.Generic.List[string]
function L([string]$s) { $lines.Add($s) }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

L "== Relatorio SharkBlocker (PC) =="
L ("Gerado em: " + (Get-Date -Format s))
try { $os = Get-CimInstance Win32_OperatingSystem; L ("Windows: " + $os.Caption + " build " + $os.BuildNumber) } catch { L "Windows: (sem informacao)" }
L ("PowerShell: " + $PSVersionTable.PSVersion + " | Administrador: " + $isAdmin)

L ""
L "-- Servico e processos --"
try {
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
    if ($svc) { L ("Servico " + $ServiceName + ": estado=" + $svc.State + " inicio=" + $svc.StartMode + " conta=" + $svc.StartName) }
    else { L ("Servico " + $ServiceName + ": NAO instalado") }
} catch { L "Servico: (erro ao consultar)" }
foreach ($n in @("SharkBlocker", "Watchdog")) {
    $p = Get-Process -Name $n -ErrorAction SilentlyContinue
    if ($p) { L ("Processo " + $n + ": rodando (PID " + (($p | ForEach-Object { $_.Id }) -join ",") + ")") } else { L ("Processo " + $n + ": nao encontrado") }
}

L ""
L "-- Modo seguro (chaves SafeBoot) --"
foreach ($k in @("Minimal", "Network")) {
    $path = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$k\$ServiceName"
    L ("SafeBoot\" + $k + ": " + $(if (Test-Path $path) { "presente" } else { "AUSENTE" }))
}

L ""
L "-- Pasta de dados (somente nomes e tamanhos) --"
try {
    Get-ChildItem -Path $dataDir -ErrorAction Stop | ForEach-Object { L ("  " + $_.Name + "  " + $_.Length + " bytes  " + $_.LastWriteTime.ToString("s")) }
} catch { L ("(nao consegui listar " + $dataDir + ")") }

L ""
L "-- health-check --"
$hc = Join-Path $PSScriptRoot "health-check.ps1"
if ($isAdmin -and (Test-Path $hc)) {
    try { $out = & $hc *>&1 | Out-String; L ($out.TrimEnd()) } catch { L "(health-check falhou ao rodar)" }
} elseif (-not $isAdmin) {
    L "(rode este script como Administrador para incluir o health-check)"
} else { L "(health-check.ps1 nao encontrado ao lado deste script)" }

L ""
L "-- Ultimas 150 linhas do app.log (URLs trocadas por <url>) --"
try {
    $log = Join-Path $dataDir "app.log"
    if (Test-Path $log) { Get-Content $log -Tail 150 -ErrorAction Stop | ForEach-Object { L ($_ -replace 'https?://\S+', '<url>') } }
    else { L "(app.log nao existe)" }
} catch { L "(nao consegui ler o app.log)" }

$text = $lines -join "`r`n"
Write-Host $text
if (-not $SemCopiar) {
    try { Set-Clipboard -Value $text; Write-Host "`nRelatorio copiado para a area de transferencia. Cole na conversa." -ForegroundColor Green }
    catch { Write-Host "`nNao consegui copiar; selecione o texto acima." -ForegroundColor Yellow }
}
