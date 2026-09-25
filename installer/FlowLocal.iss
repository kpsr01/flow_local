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
WelcomeLabel2=This will install [name/ver], a private, local dictation assistant.%n%nSpeech recognition and voice matching run on this PC. The app downloads its speech models when first started; an internet connection is required once.
SelectDirDesc=Where should FlowLocal be installed?
FinishedLabelNoIcons=[name] has been installed. Open Settings and enroll your voice before using the Ctrl+Win dictation shortcut.
FinishedLabel=[name] has been installed. Open Settings and enroll your voice before using the Ctrl+Win dictation shortcut.

[Tasks]
Name: "startup"; Description: "Start FlowLocal automatically when I sign in"; \
    GroupDescription: "Startup:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Keep the executable payload in a replaceable directory so upgrades cannot retain removed files.
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Atomically replace the complete executable payload on every repair or upgrade.
Type: filesandordirs; Name: "{app}\app"
; Remove retired speech and cleanup models from upgraded installs.
Type: files; Name: "{localappdata}\FlowLocal\Models\s1-mini-q4_k_m.gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\mumble-cleanup-2stage-q4_0.gguf"
Type: filesandordirs; Name: "{localappdata}\FlowLocal\Models\moonshine-streaming-medium"
Type: filesandordirs; Name: "{localappdata}\FlowLocal\Models\canary-180m-flash-gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\sotto-cleanup-lfm25-350m-q4_k_m.gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\nemotron-speech-streaming-en-0.6b-Q4_K_M.gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf"
Type: files; Name: "{localappdata}\FlowLocal\Models\LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf"

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

function GetInstalledVersion(var Version: String): Boolean;
begin
  Result := RegQueryStringValue(HKCU,
    ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1'),
    'DisplayVersion', Version);
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
      'Setup will replace its program files and install FlowLocal {#AppVersion}. ' +
      'Your history, settings, and recordings will be kept. New speech models download on first launch.';
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
