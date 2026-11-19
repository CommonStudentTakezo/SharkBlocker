@echo off
REM emergency-fix-hosts.bat
REM Rode este arquivo como Administrador (botao direito > Executar como administrador)
REM se o PC ficou sem acessar sites depois de testar o SharkBlocker.

echo ATENCAO: este arquivo FECHA o SharkBlocker e PARA o Watchdog (a protecao fica desligada). Se o problema for so "sem sites", feche esta janela e rode corrigir-dns.bat, que nao derruba nada.
echo Pressione uma tecla para continuar mesmo assim, ou feche a janela para cancelar.
pause
echo Restaurando permissoes do arquivo hosts...

REM Remove os atributos que travavam o arquivo
attrib -r -s "%WINDIR%\System32\drivers\etc\hosts"

REM Restaura as permissoes padrao do Windows explicitamente (NAO usa /reset,
REM que depende de heranca da pasta e pode deixar o arquivo pior)
icacls "%WINDIR%\System32\drivers\etc\hosts" /grant *S-1-5-18:F
icacls "%WINDIR%\System32\drivers\etc\hosts" /grant *S-1-5-32-544:F
icacls "%WINDIR%\System32\drivers\etc\hosts" /grant *S-1-5-32-545:RX
icacls "%WINDIR%\System32\drivers\etc\hosts" /remove:d *S-1-5-32-544
icacls "%WINDIR%\System32\drivers\etc\hosts" /remove:d *S-1-5-32-545

REM Para o servico do Watchdog, que ficaria tentando mexer no arquivo e
REM re-quebrando tudo enquanto voce nao atualiza para a versao corrigida
REM O servico nega "Parar" a Administradores (auditoria U4): volta ao descritor padrao primeiro
sc sdset SharkBlockerWatchdog "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)"
sc stop SharkBlockerWatchdog
taskkill /F /IM Watchdog.exe
taskkill /F /IM SharkBlocker.exe

REM Devolve ao DNS AUTOMATICO (DHCP) os adaptadores que ainda apontam para o filtro de DNS local (127.0.0.1 / ::1)
REM e remove as regras de firewall de DNS do SharkBlocker. (Se voce usava DNS fixo, configure de novo depois.)
powershell -NoProfile -NonInteractive -Command "Get-NetAdapter | ForEach-Object { $c=@((Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue).ServerAddresses); if (($c -contains '127.0.0.1') -or ($c -contains '::1')) { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses } }"
powershell -NoProfile -NonInteractive -Command "Get-NetFirewallRule -DisplayName 'SharkBlocker-DNS-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue"
del "%ProgramData%\SharkBlocker\dns-backup.json" 2>nul

REM Remove as regras de firewall que o bloqueio criou (SharkBlocker-FW-*), se houver
powershell -NoProfile -NonInteractive -Command "Get-NetFirewallRule -DisplayName 'SharkBlocker-FW-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue"

ipconfig /flushdns

echo.
echo Pronto. Teste acessar um site agora. O servico do Watchdog foi parado
echo de proposito - so reinstale/rode o app de novo depois de pegar a
echo versao corrigida.
pause
