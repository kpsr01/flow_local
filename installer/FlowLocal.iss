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
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
UninstallDisplayName={#AppName}
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
UninstallDisplayIcon={app}\app\{#AppExeName}
CloseApplications=yes
RestartApplications=no
UsePreviousTasks=yes
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
; Keep the executable payload in a replaceable directory so upgrades cannot retain removed files.
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
; Model URLs are revision-pinned and every download is SHA-256 verified.
Source: "https://huggingface.co/handy-computer/nemotron-speech-streaming-en-0.6b-gguf/resolve/a5d84897102d97e3bd7bd60e9288187ec265c781/nemotron-speech-streaming-en-0.6b-Q4_K_M.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "nemotron-speech-streaming-en-0.6b-Q4_K_M.gguf"; ExternalSize: 475436032; \
    Hash: "dc959ca31499b114e395c44eb4f0778968f20e5cfb03305a08a39925b2da8e1e"; \
    Flags: external download ignoreversion; Check: Not NemotronModelPresent()
Source: "https://huggingface.co/LiquidAI/LFM2.5-1.2B-Instruct-GGUF/resolve/6767265158422fb8a19c62ceb45f16f05363615b/LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf"; ExternalSize: 695755488; \
    Hash: "bb741ebb106d543e9de114b843a3d3d73d51c74b5801e69da2abde821a0cb3e1"; \
    Flags: external download ignoreversion; Check: Not CleanupModelPresent()
Source: "https://huggingface.co/LiquidAI/LFM2.5-1.2B-Instruct-DSpark-GGUF/resolve/9235d674d3bbda8a775ca42a4275d11a8c0ab008/LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf"; \
    DestDir: "{localappdata}\FlowLocal\Models"; DestName: "LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf"; ExternalSize: 175848992; \
    Hash: "5cf9bb2947638dd74a47b486b817f407831c0da420aeebb6973fb66c25af51e4"; \
    Flags: external download ignoreversion; Check: Not DsparkModelPresent()

[InstallDelete]
; Atomically replace the complete executable payload on every repair or upgrade.
Type: filesandordirs; Name: "{app}\app"
; Remove retired speech and cleanup models from upgraded installs.
Type: files; Name: "{localappdata}\FlowLocal\Models\s1-mini-q4_k_m.gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\mumble-cleanup-2stage-q4_0.gguf"
Type: filesandordirs; Name: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"
Type: filesandordirs; Name: "{localappdata}\FlowLocal\Models\canary-180m-flash-gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\sotto-cleanup-lfm25-350m-q4_k_m.gguf"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\FlowLocal"
Type: files; Name: "{%TEMP}\FlowLocal-update-*-setup.exe"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\app\{#AppExeName}"; IconFilename: "{app}\app\{#AppExeName}"; IconIndex: 0; AppUserModelID: "FlowLocal.App"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\app\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\app\{#AppExeName}"; Parameters: "--background"; Tasks: startup

[Run]
Filename: "{app}\app\{#AppExeName}"; Description: "Launch {#AppName} now"; \
    Flags: nowait postinstall skipifsilent
Filename: "{app}\app\{#AppExeName}"; Flags: nowait skipifnotsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C9329BA4-50BA-41F2-A88F-7A88223BEE9E}_is1';
  NemotronModel = '{localappdata}\FlowLocal\Models\nemotron-speech-streaming-en-0.6b-Q4_K_M.gguf';
  CleanupModel = '{localappdata}\FlowLocal\Models\LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf';
  DsparkModel = '{localappdata}\FlowLocal\Models\LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf';

function FileHasSize(const Path: String; const ExpectedSize: Int64): Boolean;
var
  ActualSize: Int64;
begin
  Result := FileSize64(ExpandConstant(Path), ActualSize) and (ActualSize = ExpectedSize);
end;

function NemotronModelPresent(): Boolean;
begin
  Result := FileHasSize(NemotronModel, 475436032);
end;

function CleanupModelPresent(): Boolean;
begin
  Result := FileHasSize(CleanupModel, 695755488);
end;

function DsparkModelPresent(): Boolean;
begin
  Result := FileHasSize(DsparkModel, 175848992);
end;

function GetInstalledVersion(var Version: String): Boolean;
begin
  Result := RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Version);
end;

function InitializeSetup(): Boolean;
var
  InstalledVersion: String;
  InstalledVersionNumber, SetupVersionNumber: Int64;
begin
  Result := True;
  if GetInstalledVersion(InstalledVersion) and
     StrToVersion(InstalledVersion, InstalledVersionNumber) and
     StrToVersion('{#AppVersion}', SetupVersionNumber) and
     (ComparePackedVersion(InstalledVersionNumber, SetupVersionNumber) > 0) then
  begin
    SuppressibleMsgBox(
      'FlowLocal ' + InstalledVersion + ' is already installed. This older setup will not replace it.',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure InitializeWizard();
var
  InstalledVersion: String;
begin
  if GetInstalledVersion(InstalledVersion) then
    WizardForm.WelcomeLabel2.Caption :=
      'FlowLocal ' + InstalledVersion + ' is already installed.'#13#10#13#10 +
      'Setup will remove its old program files and install FlowLocal {#AppVersion}. ' +
      'Your history, settings, recordings, and downloaded models will be kept.';
end;

procedure RegisterExtraCloseApplicationsResources();
begin
  { Register the legacy root executable for the one-time payload migration. }
  RegisterExtraCloseApplicationsResource(False, ExpandConstant('{app}\{#AppExeName}'));
end;

function IsUninstallerFile(const Name: String): Boolean;
begin
  Result :=
    WildcardMatch(Name, 'unins*.exe') or
    WildcardMatch(Name, 'unins*.dat') or
    WildcardMatch(Name, 'unins*.msg');
end;

procedure RemoveLegacyPayload();
var
  FindRec: TFindRec;
  Item: String;
  IsDirectory: Boolean;
begin
  if not FileExists(ExpandConstant('{app}\{#AppExeName}')) then Exit;

  if FindFirst(ExpandConstant('{app}\*'), FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
         not SameText(FindRec.Name, 'app') and not IsUninstallerFile(FindRec.Name) then
      begin
        Item := PathCombine(ExpandConstant('{app}'), FindRec.Name);
        IsDirectory := (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0;
        if not DelTree(Item, IsDirectory, True, True) then
          Log('Could not remove legacy payload item: ' + Item);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RemoveLegacyPayload();
end;
