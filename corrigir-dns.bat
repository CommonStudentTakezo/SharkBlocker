@echo off
REM corrigir-dns.bat  -  conserto RAPIDO de "sem sites / DNS_PROBE_FINISHED_NXDOMAIN".
REM Rode como Administrador. NAO fecha o SharkBlocker nem para o Watchdog (o emergency-fix-hosts.bat faz isso e derruba a protecao).
REM O que faz: DNS dos adaptadores que apontam para o filtro volta ao automatico, remove as regras de firewall de DNS do SharkBlocker,
REM libera a escrita/leitura do hosts (so tira a NEGACAO) e limpa o cache de DNS.
echo Corrigindo o DNS sem fechar o SharkBlocker...
powershell -NoProfile -NonInteractive -Command "Get-NetAdapter | ForEach-Object { $c=@((Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue).ServerAddresses); if (($c | Where-Object { $_ -eq '127.0.0.1' -or $_ -eq '::1' }).Count -gt 0) { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses } }"
powershell -NoProfile -NonInteractive -Command "Get-NetFirewallRule -DisplayName 'SharkBlocker-DNS-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue"
powershell -NoProfile -NonInteractive -Command "$p=Join-Path $env:WINDIR 'System32\drivers\etc\hosts'; $a=Get-Acl $p; $a.Access | Where-Object { $_.AccessControlType -eq 'Deny' -and -not $_.IsInherited } | ForEach-Object { [void]$a.RemoveAccessRuleSpecific($_) }; Set-Acl $p $a"
ipconfig /flushdns
echo.
echo Pronto. Teste um site. Se continuar sem sites, rode diagnostico-dns.ps1 (como Administrador) e envie o diagnostico-dns.txt.
pause
