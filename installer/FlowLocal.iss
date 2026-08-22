#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
#define AppName "FlowLocal"
#define AppPublisher "FlowLocal"
#define AppExeName "FlowLocal.App.exe"

[Setup]
AppId={{C9329BA4-50BA-41F2-A88F-7A88223BEE9E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=FlowLocal-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no
UsePreviousTasks=no
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver], a private, local dictation assistant.%n%nEverything runs on this PC: speech recognition and text cleanup never touch the cloud.%n%nThe installer also sets up the Foundry Local runtime and downloads the speech models (roughly 900 MB total). An internet connection is required once.
SelectDirDesc=Where should FlowLocal be installed?
FinishedLabelNoIcons=[name] has been installed. The dictation capsule is running in your system tray - hold Ctrl+Win anywhere and speak.
FinishedLabel=[name] has been installed. The dictation capsule is running in your system tray - hold Ctrl+Win anywhere and speak.

[Tasks]
Name: "startup"; Description: "Start FlowLocal automatically when I sign in"; \
    GroupDescription: "Startup:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; App payload
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Cleanup model (quantization-aware-distilled GGUF)
Source: "https://huggingface.co/LiquidAI/LFM2.5-350M-GGUF/resolve/main/LFM2.5-350M-QAD-Q4_0.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "LFM2.5-350M-QAD-Q4_0.gguf"; \
    ExternalSize: 219312832; Flags: external download ignoreversion; Check: Not GgufSkipDownload()

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[Code]
const
  ModelAlias = 'nemotron-speech-streaming-en-0.6b';
  FoundryAliasPath = '{localappdata}\Microsoft\WindowsApps\foundry.exe';
  GgufTarget = '{localappdata}\FlowLocal\Models\LFM2.5-350M-QAD-Q4_0.gguf';

var
  PrereqPage: TOutputProgressWizardPage;

procedure InitializeWizard();
begin
  PrereqPage := CreateOutputProgressPage(
    'Setting up local AI components',
    'Installing the Foundry Local runtime and speech models. This runs entirely on your PC.');
end;

function GgufAlreadyPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant(GgufTarget));
end;

function GgufSkipDownload(): Boolean;
begin
  Result := GgufAlreadyPresent();
end;

function FoundryInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant(FoundryAliasPath));
end;

procedure InstallFoundry();
var
  ResultCode: Integer;
begin
  if FoundryInstalled() then
  begin
    Log('Foundry Local already installed.');
    exit;
  end;

  PrereqPage.SetText('Installing Microsoft Foundry Local…', 'This can take a few minutes on first run.');
  PrereqPage.Show;
  try
    if not Exec('powershell.exe',
        '-NoProfile -ExecutionPolicy Bypass -Command "winget install --id Microsoft.FoundryLocal -e --accept-source-agreements --accept-package-agreements --silent"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      LogFmt('Failed to launch winget (code %d)', [ResultCode])
    else if ResultCode <> 0 then
      LogFmt('winget returned non-zero exit code %d', [ResultCode]);
  finally
    PrereqPage.Hide;
  end;
end;

procedure PreWarmFoundry();
var
  ResultCode: Integer;
begin
  if not FoundryInstalled() then
  begin
    Log('Skipping model predownload: Foundry Local was not installed successfully.');
    exit;
  end;

  // Starts the local service, registers execution providers (~90 s on first run),
  // and pulls the ASR model into the shared cache so the app is ready offline.
  PrereqPage.SetText('Preparing the local speech model…', 'Downloading and registering execution providers. One-time setup.');
  PrereqPage.Show;
  try
    Exec(ExpandConstant(FoundryAliasPath),
        Format('model download %s', [ModelAlias]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  finally
    PrereqPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    try
      InstallFoundry();
      PreWarmFoundry();
    except
      // Never fail the file installation over prerequisite hiccups;
      // the app surfaces precise guidance at first launch instead.
    end;
  end;
end;

function DeleteHistory: Boolean;
begin
  Result := SuppressibleMsgBox(
    'Delete FlowLocal local history and recordings?'#13#10#13#10 +
    'Choose No to preserve them for a future installation.',
    mbConfirmation, MB_YESNO, IDNO) = IDYES;
end;

procedure InitializeUninstallProgressForm();
begin
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if DeleteHistory then
      DelTree(ExpandConstant('{localappdata}\FlowLocal'), True, True, True);
end;
