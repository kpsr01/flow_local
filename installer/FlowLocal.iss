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
WelcomeLabel2=This will install [name/ver], a private, local dictation assistant.%n%nEverything runs on this PC: speech recognition and text cleanup never touch the cloud.%n%nThe installer also downloads the speech-recognition and text-cleanup models (roughly 1.35 GB total). An internet connection is required once.
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
; Nemotron Speech Streaming EN 0.6B ASR GGUF, Q4_K_M (transcribe.cpp 0.2.3)
Source: "https://huggingface.co/handy-computer/nemotron-speech-streaming-en-0.6b-gguf/resolve/main/nemotron-speech-streaming-en-0.6b-Q4_K_M.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "nemotron-speech-streaming-en-0.6b-Q4_K_M.gguf"; ExternalSize: 475436032; \
    Flags: external download ignoreversion; Check: Not NemotronModelPresent()
; LFM2.5-1.2B-Instruct QAD Q4_0 cleanup target
Source: "https://huggingface.co/LiquidAI/LFM2.5-1.2B-Instruct-GGUF/resolve/main/LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf"; ExternalSize: 695755488; \
    Flags: external download ignoreversion; Check: Not CleanupModelPresent()
; Optional LFM2.5 DSpark Q4_K_M draft sidecar
Source: "https://huggingface.co/LiquidAI/LFM2.5-1.2B-Instruct-DSpark-GGUF/resolve/main/LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf"; ExternalSize: 175848992; \
    Flags: external download ignoreversion; Check: Not DsparkModelPresent()

[InstallDelete]
; Remove retired speech and cleanup models from upgraded installs.
Type: files; Name: "{localappdata}\FlowLocal\Models\s1-mini-q4_k_m.gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\mumble-cleanup-2stage-q4_0.gguf"
Type: filesandordirs; Name: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"
Type: files; Name: "{localappdata}\FlowLocal\Models\canary-180m-flash-gguf\canary-180m-flash-q4_k_m.gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\sotto-cleanup-lfm25-350m-q4_k_m.gguf"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; IconIndex: 0; AppUserModelID: "FlowLocal.App"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Parameters: "--background"; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; \
    Flags: nowait postinstall

[Code]
const
  NemotronModel = '{localappdata}\FlowLocal\Models\nemotron-speech-streaming-en-0.6b-Q4_K_M.gguf';
  CleanupModel = '{localappdata}\FlowLocal\Models\LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf';
  DsparkModel = '{localappdata}\FlowLocal\Models\LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf';

function NemotronModelPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant(NemotronModel));
end;

function CleanupModelPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant(CleanupModel));
end;

function DsparkModelPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant(DsparkModel));
end;

function DeleteHistory: Boolean;
begin
  if WizardSilent() then
    { In-app uninstall already confirmed full removal; silent means delete everything. }
    Result := True
  else
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
