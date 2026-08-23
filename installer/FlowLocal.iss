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
WelcomeLabel2=This will install [name/ver], a private, local dictation assistant.%n%nEverything runs on this PC: speech recognition and text cleanup never touch the cloud.%n%nThe installer also downloads the speech-recognition and text-cleanup models (roughly 500 MB total). An internet connection is required once.
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
; Moonshine streaming-medium ONNX graphs + tokenizer (MIT license)
Source: "https://huggingface.co/moonshine-ai/moonshine-streaming/resolve/main/onnx/medium/frontend.onnx"; \
    DestDir: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"; DestName: "frontend.onnx"; ExternalSize: 47458770; \
    Flags: external download ignoreversion; Check: Not MoonshineModelPresent()
Source: "https://huggingface.co/moonshine-ai/moonshine-streaming/resolve/main/onnx/medium/encoder.onnx"; \
    DestDir: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"; DestName: "encoder.onnx"; ExternalSize: 94664836; \
    Flags: external download ignoreversion; Check: Not MoonshineModelPresent()
Source: "https://huggingface.co/moonshine-ai/moonshine-streaming/resolve/main/onnx/medium/adapter.onnx"; \
    DestDir: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"; DestName: "adapter.onnx"; ExternalSize: 14560169; \
    Flags: external download ignoreversion; Check: Not MoonshineModelPresent()
Source: "https://huggingface.co/moonshine-ai/moonshine-streaming/resolve/main/onnx/medium/cross_kv.onnx"; \
    DestDir: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"; DestName: "cross_kv.onnx"; ExternalSize: 11595723; \
    Flags: external download ignoreversion; Check: Not MoonshineModelPresent()
Source: "https://huggingface.co/moonshine-ai/moonshine-streaming/resolve/main/onnx/medium/decoder_kv.onnx"; \
    DestDir: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"; DestName: "decoder_kv.onnx"; ExternalSize: 125780753; \
    Flags: external download ignoreversion; Check: Not MoonshineModelPresent()
Source: "https://huggingface.co/moonshine-ai/moonshine-streaming/resolve/main/onnx/medium/tokenizer.json"; \
    DestDir: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"; DestName: "tokenizer.json"; ExternalSize: 1985533; \
    Flags: external download ignoreversion; Check: Not MoonshineModelPresent()
Source: "https://huggingface.co/LiquidAI/LFM2.5-350M-GGUF/resolve/main/LFM2.5-350M-QAD-Q4_0.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "LFM2.5-350M-QAD-Q4_0.gguf"; ExternalSize: 219312832; \
    Flags: external download ignoreversion; Check: Not GgufSkipDownload()

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
  MoonshineModelDir = '{localappdata}\FlowLocal\Models\moonshine-streaming-medium';
  GgufTarget = '{localappdata}\FlowLocal\Models\LFM2.5-350M-QAD-Q4_0.gguf';

function MoonshineModelPresent(): Boolean;
begin
  Result :=
    FileExists(ExpandConstant(MoonshineModelDir + '\frontend.onnx')) and
    FileExists(ExpandConstant(MoonshineModelDir + '\encoder.onnx')) and
    FileExists(ExpandConstant(MoonshineModelDir + '\adapter.onnx')) and
    FileExists(ExpandConstant(MoonshineModelDir + '\cross_kv.onnx')) and
    FileExists(ExpandConstant(MoonshineModelDir + '\decoder_kv.onnx')) and
    FileExists(ExpandConstant(MoonshineModelDir + '\tokenizer.json'));
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
