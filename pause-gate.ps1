# pause-gate.ps1
#
# PORTAO de seguranca para desligar as protecoes (usado por protection-pause.ps1, quick-install.ps1 e
# pelo Installer.iss ao atualizar por cima). Antes, bastava rodar o script e digitar ATUALIZAR (ou
# passar -Force) para derrubar servico, app e protecao de pasta - inclusive no meio de um bloqueio.
#
# Regras (todas fail-closed):
#   1. Bloqueio temporizado ou trava de tela em andamento: RECUSA, sem excecao e sem senha.
#      (O bloqueio "sem cancelamento" tambem vale contra este script: espere acabar.)
#   2. Fora de bloqueio, exige (exceto via quick-install, que usa -SkipPassword) a SENHA do SharkBlocker (a mesma do app), conferida contra o hash
#      PBKDF2 gravado (espelho assinado no Registro; se faltar, o do state.json). 3 tentativas.
#   3. So se NUNCA houve senha (primeira instalacao), pede a palavra ATUALIZAR.
#   4. Se o state.json existe mas esta ilegivel, RECUSA.
#
# Uso: ". .\pause-gate.ps1; if (-not (Test-PauseAllowed)) { exit 1 }"
# Automacao (instalador): a senha pode vir na variavel de ambiente SHARKBLOCKER_PAUSE_PASSWORD,
# que e apagada logo apos a leitura. Isso NAO dispensa a senha.
#
# Limite honesto: quem e Administrador ainda pode usar sc.exe/icacls/regedit na mao, ou o modo de
# seguranca. Este portal fecha o caminho FACIL (o script de atualizacao), nao o dificil.
#
# OBS: so caracteres ASCII, de proposito (PowerShell 5.1 le .ps1 sem BOM como ANSI).

$script:GateDataDir = Join-Path $env:ProgramData "SharkBlocker"
$script:GateRegPath = "HKLM:\SOFTWARE\SharkBlocker"

function Get-GateStateText {
    $f = Join-Path $script:GateDataDir "state.json"
    try { if (Test-Path -LiteralPath $f) { return [System.IO.File]::ReadAllText($f) } } catch { }
    return $null
}

function Get-GateJsonString($text, $name) {
    if (-not $text) { return $null }
    $m = [regex]::Match($text, '"' + $name + '"\s*:\s*"([^"]*)"')
    if ($m.Success -and $m.Groups[1].Value) { return $m.Groups[1].Value }
    return $null
}

# Maior "ate quando" encontrado entre JSON e Registro (qualquer valor futuro bloqueia).
function Get-ActiveLockUntil {
    $now = [DateTime]::UtcNow
    $found = @()
    $text = Get-GateStateText
    foreach ($name in @("LockedUntilUtc", "ScreenLockUntilUtc")) {
        $s = Get-GateJsonString $text $name
        if ($s) {
            try {
                $d = [DateTime]::Parse($s, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
                $found += $d.ToUniversalTime()
            } catch { }
        }
    }
    foreach ($name in @("LockedUntilUtcTicks", "ScreenLockTicks")) {
        try {
            $v = (Get-ItemProperty -Path $script:GateRegPath -Name $name -ErrorAction Stop).$name
            $ticks = 0L
            if ([long]::TryParse([string]$v, [ref]$ticks) -and $ticks -gt 0 -and $ticks -le [DateTime]::MaxValue.Ticks) {
                $found += (New-Object DateTime -ArgumentList $ticks, ([DateTimeKind]::Utc))
            }
        } catch { }
    }
    $future = @($found | Where-Object { $_ -gt $now } | Sort-Object -Descending)
    if ($future.Count -gt 0) { return $future[0] }
    return $null
}

function Get-StoredPasswordHash {
    try {
        $v = (Get-ItemProperty -Path $script:GateRegPath -Name "PasswordHashMirror" -ErrorAction Stop).PasswordHashMirror
        if ($v) { return [string]$v }
    } catch { }
    return (Get-GateJsonString (Get-GateStateText) "PasswordHash")
}

# Confere a senha contra "iteracoes.salt64.hash64" (PBKDF2-SHA256, igual ao PasswordManager do app).
function Test-PasswordAgainstHash($plain, $stored) {
    try {
        $parts = $stored.Split('.')
        if ($parts.Count -ne 3) { return $false }
        $iter = [int]$parts[0]
        $salt = [Convert]::FromBase64String($parts[1])
        $expected = [Convert]::FromBase64String($parts[2])
        $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($plain, $salt, $iter, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        $actual = $kdf.GetBytes($expected.Length)
        $diff = $actual.Length -bxor $expected.Length
        for ($i = 0; $i -lt $expected.Length; $i++) { $diff = $diff -bor ($actual[$i] -bxor $expected[$i]) }
        return ($diff -eq 0)
    } catch { return $false }
}

function Read-GatePassword($prompt) {
    $env1 = [Environment]::GetEnvironmentVariable("SHARKBLOCKER_PAUSE_PASSWORD")
    if ($env1) {
        [Environment]::SetEnvironmentVariable("SHARKBLOCKER_PAUSE_PASSWORD", $null)
        return $env1
    }
    $secure = Read-Host $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Write-GateLog($msg) {
    try {
        New-Item -ItemType Directory -Force -Path $script:GateDataDir | Out-Null
        Add-Content -Path (Join-Path $script:GateDataDir "pause-gate-log.txt") -Value ("{0:u}  {1}" -f [DateTime]::UtcNow, $msg)
    } catch { }
}

# Devolve $true so se for permitido pausar as protecoes agora.
# -SkipPassword: usado so pelo quick-install.ps1 (instalacao de desenvolvimento, sem pedir a senha).
# A regra 1 (bloqueio/trava ativos = recusa) continua valendo.
function Test-PauseAllowed {
    param([switch]$SkipPassword)
    $until = Get-ActiveLockUntil
    if ($until) {
        $local = $until.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
        Write-Host ""
        Write-Host "RECUSADO: ha um bloqueio/trava ativo ate $local." -ForegroundColor Red
        Write-Host "As protecoes nao podem ser pausadas durante o bloqueio, nem com senha. Tente depois." -ForegroundColor Red
        Write-GateLog "Pausa RECUSADA: bloqueio ativo ate $($until.ToString('o'))."
        return $false
    }

    if ($SkipPassword) {
        Write-GateLog "Pausa AUTORIZADA (quick-install, sem senha; nenhum bloqueio ativo)."
        return $true
    }

    $hash = Get-StoredPasswordHash
    if ($hash) {
        for ($try = 1; $try -le 3; $try++) {
            $pw = Read-GatePassword "Senha do SharkBlocker (tentativa $try de 3)"
            if ($pw -and (Test-PasswordAgainstHash $pw $hash)) {
                Write-GateLog "Pausa AUTORIZADA (senha correta)."
                return $true
            }
            Write-Host "Senha incorreta." -ForegroundColor Yellow
            Start-Sleep -Seconds 5
        }
        Write-Host "RECUSADO: senha incorreta 3 vezes." -ForegroundColor Red
        Write-GateLog "Pausa RECUSADA: senha incorreta 3 vezes."
        return $false
    }

    # Sem hash: so vale numa instalacao que nunca definiu senha. Se o state.json existe mas nao da
    # para le-lo, pode ser adulteracao: recusa (fail-closed).
    if ((Test-Path -LiteralPath (Join-Path $script:GateDataDir "state.json")) -and -not (Get-GateStateText)) {
        Write-Host "RECUSADO: nao consegui ler o estado gravado. Abra o SharkBlocker e confira." -ForegroundColor Red
        Write-GateLog "Pausa RECUSADA: estado ilegivel."
        return $false
    }
    $answer = [Environment]::GetEnvironmentVariable("SHARKBLOCKER_PAUSE_PASSWORD")
    if ($answer) { [Environment]::SetEnvironmentVariable("SHARKBLOCKER_PAUSE_PASSWORD", $null) }
    else { $answer = Read-Host "Nenhuma senha definida ainda (primeira instalacao). Digite ATUALIZAR para continuar" }
    if ($answer -eq "ATUALIZAR") { Write-GateLog "Pausa AUTORIZADA (sem senha definida)."; return $true }
    Write-Host "Cancelado."
    return $false
}
