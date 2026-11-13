<#
 Diagnostico SOMENTE-LEITURA do DNS do PC. Rode como Administrador, ENQUANTO os sites estao dando erro de DNS
 (antes de rodar o emergency-fix-hosts.bat). Nao altera nada. Grava o relatorio em diagnostico-dns.txt (na pasta do script).
 Mostra servidores DNS, quem escuta a porta 53, o resultado de consultas de teste e as linhas "DNS" do log (sem enderecos visitados).
 NAO TESTADO pelo autor (sem Windows no ambiente de desenvolvimento).
#>
$ErrorActionPreference = "Continue"
$out = Join-Path $PSScriptRoot "diagnostico-dns.txt"
$lines = New-Object System.Collections.Generic.List[string]
function Add-Line($t) { $script:lines.Add([string]$t); Write-Host $t }
function Section($t) { Add-Line ""; Add-Line "=== $t ===" }
function Try-Run($t, [scriptblock]$b) { try { & $b | Out-String -Width 200 | ForEach-Object { Add-Line $_.TrimEnd() } } catch { Add-Line "(erro em $t : $($_.Exception.Message))" } }

Add-Line ("Data: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))

Section "1) Servidores DNS de cada adaptador (127.0.0.1 / ::1 = filtro do SharkBlocker)"
Try-Run "dns" { Get-NetAdapter | Where-Object { $_.Status -eq "Up" } | ForEach-Object {
    $a = $_; Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue |
    ForEach-Object { "{0,-28} {1,-5} {2}" -f $a.Name, $_.AddressFamily, ($_.ServerAddresses -join ", ") } } }

Section "2) Quem escuta a porta 53 (UDP e TCP)"
Try-Run "udp53" { Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; "UDP {0}:{1}  {2} (pid {3})" -f $_.LocalAddress, $_.LocalPort, $p.ProcessName, $_.OwningProcess } }
Try-Run "tcp53" { Get-NetTCPConnection -LocalPort 53 -State Listen -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; "TCP {0}:{1}  {2} (pid {3})" -f $_.LocalAddress, $_.LocalPort, $p.ProcessName, $_.OwningProcess } }

Section "3) Consultas de teste (Status: Success = respondeu; 'DNS name does not exist' = NXDOMAIN; timeout = ninguem respondeu)"
foreach ($t in @(@("google.com", "127.0.0.1"), @("google.com", "::1"), @("google.com", "1.1.1.3"), @("google.com", "1.0.0.3"), @("google.com", "8.8.8.8"), @("google.com", ""))) {
    $label = if ($t[1]) { "google.com via $($t[1])" } else { "google.com via DNS padrao do Windows" }
    try {
        if ($t[1]) { $r = Resolve-DnsName -Name $t[0] -Server $t[1] -Type A -DnsOnly -QuickTimeout -ErrorAction Stop }
        else       { $r = Resolve-DnsName -Name $t[0] -Type A -ErrorAction Stop }
        Add-Line ("{0,-45} OK  -> {1}" -f $label, (($r | Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress }) -join ", "))
    } catch { Add-Line ("{0,-45} FALHOU -> {1}" -f $label, $_.Exception.Message) }
}

Section "4) Regras de firewall do SharkBlocker"
Try-Run "fw" { Get-NetFirewallRule -DisplayName "SharkBlocker-*" -ErrorAction SilentlyContinue | Select-Object DisplayName, Enabled, Action, Direction | Format-Table -AutoSize }

Section "5) Servico e processos"
Try-Run "svc" { Get-Service SharkBlockerWatchdog -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType | Format-Table -AutoSize }
Try-Run "proc" { Get-Process -Name Watchdog, SharkBlocker -ErrorAction SilentlyContinue | Select-Object Name, Id, StartTime | Format-Table -AutoSize }

Section "6) Backup do DNS original (dns-backup.json) - existe?"
$bak = Join-Path $env:ProgramData "SharkBlocker\dns-backup.json"
if (Test-Path -LiteralPath $bak) { Add-Line "EXISTE (o Watchdog ainda considera o DNS 'trocado'):"; Try-Run "bak" { Get-Content -Raw -LiteralPath $bak } } else { Add-Line "nao existe" }

Section "7) Linhas 'DNS' do log do SharkBlocker (ultimas 40)"
$log = Join-Path $env:ProgramData "SharkBlocker\app.log"
if (Test-Path -LiteralPath $log) { Try-Run "log" { Select-String -LiteralPath $log -Pattern "DNS" -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -Last 40 | ForEach-Object { $_.Line } } } else { Add-Line "app.log nao encontrado em $log" }

Section "8) Navegador: DNS seguro (DoH) configurado por politica?"
foreach ($k in @("HKLM:\SOFTWARE\Policies\Google\Chrome", "HKLM:\SOFTWARE\Policies\Microsoft\Edge")) {
    if (Test-Path $k) { Try-Run $k { Get-ItemProperty $k | Select-Object DnsOverHttpsMode, DnsOverHttpsTemplates | Format-List } } else { Add-Line "$k : sem politica" }
}
Add-Line "Confira tambem no Chrome: chrome://settings/security > 'Usar DNS seguro'. Se estiver com um provedor personalizado, desligue ou deixe 'Automatico'."

$lines | Set-Content -LiteralPath $out -Encoding UTF8
Write-Host ""
Write-Host "Relatorio gravado em: $out" -ForegroundColor Green
Write-Host "Cole o conteudo na conversa (nao tem enderecos visitados nem senhas)." -ForegroundColor Green
