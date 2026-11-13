; ============================================================================
; Installer.iss - script do Inno Setup (https://jrsoftware.org/isinfo.php)
;
; Gera um instalador unico (SharkBlocker-Setup.exe) que:
;  - Copia App, Watchdog e a extensao de navegador para Arquivos de Programas
;  - Cria atalhos no Menu Iniciar / Area de Trabalho
;  - Roda o app ao final (que se encarrega de instalar o Servico do Windows)
;  - Aparece normalmente em "Adicionar ou Remover Programas"
;  - MAS: o desinstalador gerado (unins000.exe) NAO apaga nada sozinho.
;    Antes de tudo, ele chama "SharkBlocker.exe --uninstall" e so continua
;    se o app confirmar (senha correta / sem bloqueio ativo). Isso fecha a
;    brecha que existiria se alguem pudesse desinstalar direto pelo Painel de
;    Controle sem passar pela senha.
;
; NOTA: este arquivo usa apenas caracteres ASCII de proposito (sem acentos).
; Alguns leitores/editores podem interpretar UTF-8 sem BOM incorretamente
; dependendo da configuracao regional do Windows, corrompendo acentos -
; evitar acentos aqui elimina esse risco por completo.
;
; COMO COMPILAR (no Windows, com Inno Setup instalado):
;   1. Baixe e instale o Inno Setup em https://jrsoftware.org/isdl.php
;   2. Publique o projeto como self-contained (gera os .exe sem depender de
;      instalar o .NET separadamente):
;        dotnet publish App -c Release -r win-x64 --self-contained true
;        dotnet publish Watchdog -c Release -r win-x64 --self-contained true
;   3. Abra este arquivo (Installer.iss) no Inno Setup Compiler e clique em
;      Build > Compile (ou rode via linha de comando: iscc Installer.iss)
;   4. O instalador final sai em Output\SharkBlocker-Setup.exe
; ============================================================================

#define MyAppName "SharkBlocker"
#define MyAppVersion "1.3"
#define MyAppExeName "SharkBlocker.exe"

[Setup]
AppId={{B7B3A8C1-4C2E-4B8B-9B7B-COLDTURKEYCLONE}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\SharkBlocker
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=SharkBlocker-Setup
Compression=lzma
SolidCompression=yes
UninstallDisplayIcon={app}\App\{#MyAppExeName}
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "App\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Watchdog\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}\Watchdog"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "BrowserExtension\*"; DestDir: "{app}\BrowserExtension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ExtensionSigningKey\*"; DestDir: "{app}\ExtensionSigningKey"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "protection-pause.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "pause-gate.ps1"; DestDir: "{app}"; Flags: ignoreversion
; Copia temporaria do portao: o instalador o usa ANTES de copiar arquivos (ver PrepareToInstall).
Source: "pause-gate.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\App\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\App\{#MyAppExeName}"

[Run]
Filename: "{app}\App\{#MyAppExeName}"; Description: "Abrir o {#MyAppName} agora"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\SharkBlocker"

[Code]
// ---------------------------------------------------------------------------
// ATUALIZAR POR CIMA de uma instalacao existente.
// O Watchdog.exe fica travado enquanto o servico roda, e varias coisas reagem a quem tenta parar
// ou trocar os arquivos: o servico reinicia sozinho em 5 s ("sc failure"), o app reinicia o
// servico ao abrir, o Watchdog reabre o app, e a pasta tem a exclusao negada. Antes de copiar,
// desarmamos isso na ordem certa; depois de copiar, religamos. (Mesma sequencia de
// protection-pause.ps1.) Falhas aqui sao ignoradas de proposito: numa primeira instalacao o
// servico, a tarefa e os processos simplesmente nao existem.
// ---------------------------------------------------------------------------
function SetEnvironmentVariable(lpName, lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

var
  PausePage: TInputQueryWizardPage;

// Ja existe um SharkBlocker nesta maquina? (estado gravado ou servico registrado)
function HasExistingInstall: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\SharkBlocker\state.json')) or
            RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\SharkBlockerWatchdog');
end;

// Atualizar por cima desliga as protecoes, entao exige a senha (mesmo portao do protection-pause.ps1).
procedure InitializeWizard;
begin
  PausePage := CreateInputQueryPage(wpWelcome, 'Atualizacao protegida',
    'Ja existe um SharkBlocker instalado.',
    'Para atualizar por cima, digite a senha do SharkBlocker. Se voce nunca definiu uma senha, digite ATUALIZAR.');
  PausePage.Add('Senha:', True);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PausePage <> nil) and (PageID = PausePage.ID) and (not HasExistingInstall);
end;

procedure RunHidden(const Exe, Params: String);
var
  ResultCode: Integer;
begin
  Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppDir, Pw: String;
  ResultCode: Integer;
  Ran: Boolean;
begin
  Result := '';
  AppDir := ExpandConstant('{app}');

  // PORTAO: antes de desligar qualquer protecao numa instalacao existente. Recusa durante bloqueio
  // ou trava de tela e exige a senha. Qualquer falha em rodar o portao = RECUSA (fail-closed).
  if HasExistingInstall then
  begin
    ExtractTemporaryFile('pause-gate.ps1');
    Pw := PausePage.Values[0];
    if Pw = '' then Pw := ExpandConstant('{param:PausePass|}'); // instalacao silenciosa: /PausePass=...
    if Pw = '' then Pw := '-';
    SetEnvironmentVariable('SHARKBLOCKER_PAUSE_PASSWORD', Pw);
    Ran := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "& { . ''' + ExpandConstant('{tmp}\pause-gate.ps1') +
      '''; if (Test-PauseAllowed) { exit 0 } else { exit 1 } }"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    SetEnvironmentVariable('SHARKBLOCKER_PAUSE_PASSWORD', '');
    if (not Ran) or (ResultCode <> 0) then
    begin
      Result := 'Atualizacao RECUSADA: ha um bloqueio ativo, a senha esta incorreta ou nao foi possivel confere-la. ' +
                'Detalhes em C:\ProgramData\SharkBlocker\pause-gate-log.txt. Nada foi alterado.';
      Exit;
    end;
  end;

  // 1) impede o reinicio automatico do servico; 2) impede o app de abrir no logon
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'config SharkBlockerWatchdog start= disabled');
  RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Change /TN "SharkBlockerApp" /DISABLE');

  // 3) para o servico ANTES do app (senao o Watchdog reabre o app) e espera ele terminar
  // (U4: o servico nega "Parar" a Administradores; depois da senha volta ao descritor padrao)
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'sdset SharkBlockerWatchdog "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)"');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop SharkBlockerWatchdog');
  Sleep(5000);
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Watchdog.exe');

  // 4) agora sim encerra o app
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SharkBlocker.exe');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SharkBlocker.exe'); // nome antigo (versões anteriores)
  Sleep(1500);
  // encerramento de propósito (atualização): apaga o batimento do app, senão o Watchdog/o app novo
  // tratariam isto como fechamento forçado e travariam a tela por 30 min
  DeleteFile(ExpandConstant('{commonappdata}\SharkBlocker\app-alive.flag'));
  DeleteFile(ExpandConstant('{commonappdata}\SharkBlocker\watchdog-running.flag'));

  // 5) tira a negacao de exclusao da pasta (o Watchdog a reaplica depois)
  RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + AppDir + '" /remove:d *S-1-5-32-544 /T /C /Q');
  RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + AppDir + '" /remove:d *S-1-5-32-545 /T /C /Q');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Religa o servico e a tarefa de logon. Na primeira instalacao o servico ainda nao existe:
    // o app (aberto pelo [Run] abaixo) o instala.
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'config SharkBlockerWatchdog start= auto');
    RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Change /TN "SharkBlockerApp" /ENABLE');
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'start SharkBlockerWatchdog');
  end;
end;

function InitializeUninstall(): Boolean;
var
  AppExe: String;
  ResultCode: Integer;
begin
  Result := True;
  AppExe := ExpandConstant('{app}\App\{#MyAppExeName}');

  // Se o executavel ja nao existir mais (foi apagado manualmente por algum
  // motivo), nao tem como checar nada - deixa o Setup seguir normalmente.
  if not FileExists(AppExe) then
    exit;

  if not Exec(AppExe, '--uninstall', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Nao foi possivel iniciar a checagem de autorizacao. Desinstalacao cancelada.',
      mbError, MB_OK);
    Result := False;
    exit;
  end;

  // App.exe retorna 0 = autorizado (senha correta / sem bloqueio ativo).
  // Qualquer outro codigo = negado ou cancelado pelo usuario.
  if ResultCode <> 0 then
  begin
    Result := False;
  end;
end;
