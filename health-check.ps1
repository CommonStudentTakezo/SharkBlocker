# health-check.ps1
#
# Verifica a saude da arquitetura cooperativa do SharkBlocker (Watchdog = servico, App = interface + Guard):
#   servico, processos, batimento do app, permissoes da pasta de dados, estado/backup, senha, tarefas
#   agendadas e instalacao interrompida. So LE: nao altera nada.
#
# Uso:  .\health-check.ps1        (pede elevacao sozinho)
# Codigo de saida: 0 = tudo certo, 1 = avisos, 2 = falhas.
#
# OBS: so caracteres ASCII, de proposito (PowerShell 5.1 le .ps1 sem BOM como ANSI).

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Precisa de Administrador (para ler a pasta de dados). Reabrindo elevado..." -ForegroundColor Yellow
    Start-Process PowerShell -Verb RunAs -ArgumentList @('-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"")
    exit
}

$ErrorActionPreference = "Continue"
$ServiceName = "SharkBlockerWatchdog"
$dataDir = Join-Path $env:ProgramData "SharkBlocker"
$script:warn = 0
$script:fail = 0

function Ok($m)   { Write-Host "  [OK]    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [AVISO] $m" -ForegroundColor Yellow; $script:warn++ }
function Fail($m) { Write-Host "  [FALHA] $m" -ForegroundColor Red; $script:fail++ }
# Extrai o caminho do .exe do PathName do servico (com ou sem aspas, com ou sem argumentos).
function Get-ExePath([string]$pathName) {
    if ($pathName -match '^\s*"([^"]+)"') { return $Matches[1] }
    return $pathName.Trim()
}

function Section($m) { Write-Host ""; Write-Host $m -ForegroundColor Cyan }

# ---- 1. Servico (Watchdog) ----
Section "1. Servico (Watchdog)"
try { $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop } catch { $svc = $null }
if (-not $svc) {
    Fail "o servico $ServiceName nao esta instalado (rode install-service.ps1)"
} else {
    if ($svc.State -eq 'Running') { Ok "em execucao (PID $($svc.ProcessId))" } else { Fail "estado: $($svc.State)" }
    if ($svc.StartMode -eq 'Auto') { Ok "inicio automatico" } else { Fail "StartMode = $($svc.StartMode) (esperado Auto)" }
    if ($svc.StartName -eq 'LocalSystem') { Ok "conta LocalSystem" } else { Warn "conta = $($svc.StartName) (esperado LocalSystem)" }
    $exe = Get-ExePath $svc.PathName
    if (Test-Path -LiteralPath $exe) { Ok "binario existe: $exe" } else { Fail "binario nao existe: $exe" }
    $qf = (& sc.exe qfailure $ServiceName 2>&1 | Out-String)
    if ($qf -match '(RESTART|REINICIAR).*1000') { Ok "reinicio automatico em 1 s configurado" } else { Warn "reinicio automatico nao configurado como esperado (o Watchdog reaplica sozinho)" }
    # Modo seguro: sem estas chaves o servico nao sobe no modo seguro (somente leitura aqui; o Watchdog as reafirma).
    foreach ($mode in @('Minimal', 'Network')) {
        $sbVal = $null
        try { $sbVal = (Get-Item -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName" -ErrorAction Stop).GetValue('') } catch { }
        if ($sbVal -ceq 'Service') { Ok "registrado para o modo seguro (SafeBoot\$mode)" } else { Warn "sem registro no modo seguro (SafeBoot\$mode); o Watchdog recria em ~1 min" }
    }
}

# ---- 2. Processos cooperativos ----
Section "2. Processos cooperativos"
$wd = Get-Process -Name Watchdog -ErrorAction SilentlyContinue
$ap = Get-Process -Name SharkBlocker -ErrorAction SilentlyContinue
if ($wd) { Ok "Watchdog.exe vivo (PID $(($wd | Select-Object -First 1).Id))" } else { Fail "Watchdog.exe nao esta rodando" }
if ($ap) { Ok "SharkBlocker.exe vivo (PID $(($ap | Select-Object -First 1).Id)) - o Guard do app vigia o Watchdog" } else { Warn "SharkBlocker.exe nao esta rodando (o Watchdog o reabre em segundos)" }

$alive = Join-Path $dataDir "app-alive.flag"
if (Test-Path -LiteralPath $alive) {
    try {
        $ticks = [long](Get-Content -LiteralPath $alive -Raw).Trim()
        $age = ([DateTime]::UtcNow - (New-Object DateTime($ticks, 'Utc'))).TotalSeconds
        if ($age -le 20) { Ok ("batimento do app ha {0:N0} s" -f $age) } else { Warn ("ultimo batimento do app ha {0:N0} s" -f $age) }
    } catch { Warn "nao consegui ler o batimento do app" }
} else {
    Warn "sem batimento do app (app fechado ou ainda iniciando)"
}

# ---- 3. Pasta de dados: permissoes ----
Section "3. Pasta de dados ($dataDir)"
if (-not (Test-Path -LiteralPath $dataDir)) {
    Fail "a pasta de dados nao existe"
} else {
    $acl = Get-Acl -LiteralPath $dataDir
    if ($acl.AreAccessRulesProtected) { Ok "nao herda as regras de ProgramData" } else { Warn "ainda herda as regras de ProgramData (o app reaplica ao abrir)" }
    $bad = $acl.Access | Where-Object {
        $_.AccessControlType -eq 'Allow' -and
        $_.IdentityReference.Value -notmatch '(SYSTEM|SISTEMA|Administrators|Administradores)$' -and
        ($_.FileSystemRights -band ([System.Security.AccessControl.FileSystemRights]'WriteData, AppendData, Delete, ChangePermissions, TakeOwnership')) -ne 0
    }
    if ($bad) { Fail ("usuarios com escrita na pasta de dados: " + (($bad | ForEach-Object { $_.IdentityReference.Value }) -join ', ')) }
    else { Ok "somente SYSTEM e Administradores escrevem" }
}

# ---- 4. Estado, senha e backups ----
Section "4. Estado, senha e backups"
$stateFile = Join-Path $dataDir "state.json"
if (Test-Path -LiteralPath $stateFile) {
    try {
        $st = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json
        Ok "state.json e um JSON valido"
        if ($st.PasswordHash) { Ok "senha administrativa definida" } else { Fail "NENHUMA senha administrativa definida (fechar/remover/desinstalar ficam recusados ate definir)" }
    } catch { Fail "state.json ilegivel (o app restaura do backup ao abrir)" }
} else {
    Warn "state.json ainda nao existe (primeira execucao?)"
}
if (Test-Path -LiteralPath "$stateFile.bak") { Ok "state.json.bak presente" } else { Warn "sem state.json.bak ainda" }
$snaps = @(Get-ChildItem -LiteralPath (Join-Path $dataDir "backups") -File -ErrorAction SilentlyContinue)
if ($snaps.Count -gt 0) { Ok "$($snaps.Count) instantaneo(s) em backups\" } else { Warn "nenhum instantaneo em backups\ ainda (sao criados a cada 10 min de uso)" }
$corrupt = @(Get-ChildItem -LiteralPath $dataDir -Filter "state.json.corrupt-*" -ErrorAction SilentlyContinue)
if ($corrupt.Count -gt 0) { Warn "$($corrupt.Count) state.json corrompido(s) ja foram restaurados do backup (veja app.log)" }

# ---- 5. Tarefas agendadas ----
Section "5. Tarefas agendadas"
foreach ($t in @('SharkBlockerApp', 'SharkBlockerWatchdogRevive')) {
    & schtasks.exe /Query /TN $t 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok "tarefa $t existe" } else { Warn "tarefa $t nao existe (o app a registra ao abrir)" }
}

# ---- 6. Instalacao interrompida ----
Section "6. Instalacao"
$journal = Join-Path $dataDir "install-journal.json"
if (Test-Path -LiteralPath $journal) {
    try {
        $j = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
        if ($j.stage -in @('Complete', 'RolledBack')) { Ok "ultima instalacao: $($j.stage)" }
        else { Fail "instalacao INTERROMPIDA (etapa $($j.stage)). Rode: .\quick-install.ps1 -RecoverOnly" }
    } catch { Warn "diario de instalacao ilegivel" }
} else {
    Ok "nenhum diario de instalacao pendente"
}


# ---- 7. Filtro de DNS local (opcional) ----
Section "7. Filtro de DNS local"
$dnsBackup = Join-Path $dataDir "dns-backup.json"
$onLoopback = @()
foreach ($a in @(Get-NetAdapter -ErrorAction SilentlyContinue)) {
    $cur = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue).ServerAddresses)
    if (($cur -contains '127.0.0.1') -or ($cur -contains '::1')) { $onLoopback += $a.Name }
}
if ($onLoopback.Count -eq 0) {
    if (Test-Path -LiteralPath $dnsBackup) { Warn "ha um backup de DNS (dns-backup.json), mas nenhum adaptador aponta para o filtro: o Watchdog limpa no proximo ciclo" }
    else { Ok "filtro de DNS nao esta em uso (DNS normal)" }
} else {
    Write-Host "  adaptadores com DNS no filtro: $($onLoopback -join ', ')"
    try {
        Resolve-DnsName -Name 'cloudflare.com' -Type A -Server 127.0.0.1 -DnsOnly -ErrorAction Stop | Out-Null
        Ok "o filtro responde em 127.0.0.1"
    } catch { Fail "o DNS aponta para 127.0.0.1, mas o filtro NAO responde (PC sem internet). Rode emergency-fix-hosts.bat como Administrador" }
    $canaryBlocked = $false
    try { Resolve-DnsName -Name 'use-application-dns.net' -Type A -Server 127.0.0.1 -DnsOnly -ErrorAction Stop | Out-Null } catch { $canaryBlocked = $true }
    if ($canaryBlocked) { Ok "nome canario do Firefox (use-application-dns.net) bloqueado" } else { Warn "o nome canario do Firefox nao foi bloqueado" }
    $nRules = @(Get-NetFirewallRule -DisplayName 'SharkBlocker-DNS-*' -ErrorAction SilentlyContinue).Count
    if ($nRules -ge 6) { Ok "$nRules regras de firewall de DNS" } else { Warn "regras de firewall de DNS: $nRules de 6" }
}

Write-Host ""
if ($script:fail -gt 0) { Write-Host "Resultado: $($script:fail) falha(s), $($script:warn) aviso(s)." -ForegroundColor Red; exit 2 }
if ($script:warn -gt 0) { Write-Host "Resultado: $($script:warn) aviso(s), nenhuma falha." -ForegroundColor Yellow; exit 1 }
Write-Host "Resultado: tudo certo." -ForegroundColor Green
exit 0
