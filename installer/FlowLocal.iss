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
WelcomeLabel2=This will install [name/ver], a private, local dictation assistant.%n%nEverything runs on this PC: speech recognition and text cleanup never touch the cloud.%n%nThe installer also downloads the speech-recognition and text-cleanup models (roughly 370 MB total). An internet connection is required once.
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
; Canary 180M Flash ASR GGUF, Q4_K_M quant (CC-BY-4.0, transcribe.cpp port)
Source: "https://huggingface.co/handy-computer/canary-180m-flash-gguf/resolve/main/canary-180m-flash-Q4_K_M.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models\canary-180m-flash-gguf"; DestName: "canary-180m-flash-Q4_K_M.gguf"; ExternalSize: 139223744; \
    Flags: external download ignoreversion; Check: Not CanaryModelPresent()
Source: "https://huggingface.co/baddu/sotto-cleanup-lfm25-350m-GGUF/resolve/main/sotto-cleanup-lfm25-350m-q4_k_m.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "sotto-cleanup-lfm25-350m-q4_k_m.gguf"; ExternalSize: 229311200; \
    Flags: external download ignoreversion; Check: Not GgufSkipDownload()

[InstallDelete]
; Remove retired speech and cleanup models from upgraded installs.
Type: files; Name: "{localappdata}\FlowLocal\Models\s1-mini-q4_k_m.gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\mumble-cleanup-2stage-q4_0.gguf"
Type: filesandordirs; Name: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Parameters: "--background"; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[Code]
const
  CanaryModelDir = '{localappdata}\FlowLocal\Models\canary-180m-flash-gguf';
  GgufTarget = '{localappdata}\FlowLocal\Models\sotto-cleanup-lfm25-350m-q4_k_m.gguf';

function CanaryModelPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant(CanaryModelDir + '\canary-180m-flash-Q4_K_M.gguf'));
end;

function GgufSkipDownload(): Boolean;
begin
  Result := FileExists(ExpandConstant(GgufTarget));
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
