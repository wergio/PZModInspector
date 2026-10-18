; PZ Mod Inspector setup (Inno Setup 6.1 or later).
;
; Build after "dotnet publish" of the app and the CLI:
;   ISCC installer\PZModInspector.iss                      version read from publish\PZModInspector.exe
;   ISCC /DAppVersion=1.1.0-beta.1 installer\PZModInspector.iss
; Output: artifacts\PZModInspector-Setup-<version>.exe
;
; Per user, no admin rights: it installs in %LOCALAPPDATA%\Programs\PZModInspector. Only the .NET Desktop Runtime,
; when it is missing, is downloaded from Microsoft and installed for the whole machine, so only then Windows asks UAC.

#define AppName "PZ Mod Inspector"
#define AppExeName "PZModInspector.exe"
#define CliExeName "PZModInspectorCLI.exe"
#define PublishDir AddBackslash(SourcePath) + "..\publish"

#ifndef AppVersion
  ; "1.0.0+<commit>" in the exe: the part after "+" is not part of the version.
  #define ProductVersion GetStringFileInfo(PublishDir + "\" + AppExeName, "ProductVersion")
  #define AppVersion Pos("+", ProductVersion) > 0 ? Copy(ProductVersion, 1, Pos("+", ProductVersion) - 1) : ProductVersion
#endif
; The file version only takes numbers: 1.1.0-beta.1 -> 1.1.0.
#define NumericVersion Pos("-", AppVersion) > 0 ? Copy(AppVersion, 1, Pos("-", AppVersion) - 1) : AppVersion

; Both exes are framework-dependent: the Desktop Runtime also brings the base .NET runtime the CLI needs.
#define RuntimeMajor "10"
#define RuntimeUrl "https://aka.ms/dotnet/" + RuntimeMajor + ".0/windowsdesktop-runtime-win-x64.exe"
#define RuntimeFile "windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{9E98560D-1CF1-4074-AE4C-C8ACAE5E629C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Daniele Vergini
AppPublisherURL=https://github.com/wergio/PZModInspector
AppSupportURL=https://github.com/wergio/PZModInspector/issues
AppUpdatesURL=https://github.com/wergio/PZModInspector/releases
AppCopyright=Copyright (c) 2026 Daniele Vergini
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\PZModInspector
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The app is closed by the code below, which lets it save its settings; the Restart Manager is only a fallback.
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\assets\PZModInspector.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\artifacts
OutputBaseFilename=PZModInspector-Setup-{#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[CustomMessages]
english.InstallingRuntime=Installing the .NET {#RuntimeMajor} Desktop Runtime...
english.InstallingRuntimeDesc=PZ Mod Inspector needs it. Windows may ask for permission.
english.RuntimeDownloadFailed=The .NET {#RuntimeMajor} Desktop Runtime could not be downloaded:%n%1
english.RuntimeCancelled=The .NET {#RuntimeMajor} Desktop Runtime was not installed because the permission was denied. PZ Mod Inspector cannot run without it.
english.RuntimeFailed=The .NET {#RuntimeMajor} Desktop Runtime could not be installed (code %1). PZ Mod Inspector cannot run without it.
english.DeleteData=Do you also want to delete the settings and the Workshop cache of PZ Mod Inspector?%n%n%1
italian.InstallingRuntime=Installazione di .NET {#RuntimeMajor} Desktop Runtime...
italian.InstallingRuntimeDesc=Serve a PZ Mod Inspector. Windows potrebbe chiedere l'autorizzazione.
italian.RuntimeDownloadFailed=Non è stato possibile scaricare .NET {#RuntimeMajor} Desktop Runtime:%n%1
italian.RuntimeCancelled=.NET {#RuntimeMajor} Desktop Runtime non è stato installato perché l'autorizzazione è stata negata. Senza, PZ Mod Inspector non può funzionare.
italian.RuntimeFailed=Non è stato possibile installare .NET {#RuntimeMajor} Desktop Runtime (codice %1). Senza, PZ Mod Inspector non può funzionare.
italian.DeleteData=Vuoi cancellare anche le impostazioni e la cache del Workshop di PZ Mod Inspector?%n%n%1

[Files]
Source: "{#PublishDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\{#CliExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Tasks]
; Checked by default; on an update the choice made at the first install is kept.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;
  RuntimePage: TOutputProgressWizardPage;

function HasFramework(const DotnetDir, Name: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(DotnetDir + '\shared\' + Name + '\{#RuntimeMajor}.*', FindRec);
  if Result then
    FindClose(FindRec);
end;

function HasRuntimeIn(const DotnetDir: String): Boolean;
begin
  Result := HasFramework(DotnetDir, 'Microsoft.NETCore.App') and HasFramework(DotnetDir, 'Microsoft.WindowsDesktop.App');
end;

{ The x64 runtime: in Program Files\dotnet, or in Program Files\dotnet\x64 on Windows on ARM. }
function RuntimeInstalled: Boolean;
begin
  Result := HasRuntimeIn(ExpandConstant('{commonpf64}\dotnet')) or HasRuntimeIn(ExpandConstant('{commonpf64}\dotnet\x64'));
end;

{ Closes the installed app like the X of its window (so it saves its settings), and kills it after 15 seconds.
  Only the copy in the install folder: one started from somewhere else does not lock our files. }
procedure CloseRunningApp;
var
  ExePath, Params: String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#AppExeName}');
  if not FileExists(ExePath) then
    Exit;
  StringChangeEx(ExePath, '''', '''''', True);
  { Win32_Process gives the path of 64-bit processes to a 32-bit PowerShell too. }
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    'Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq ''' + ExePath + ''' } | ForEach-Object { ' +
    '$p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue; ' +
    'if ($p) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(15000)) { $p.Kill() } } }"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Could not run PowerShell to close the app: ' + SysErrorMessage(ResultCode));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  RuntimePage := CreateOutputProgressPage(CustomMessage('InstallingRuntime'), CustomMessage('InstallingRuntimeDesc'));
end;

{ Runs the downloaded runtime installer. Returns '' when the runtime is there, otherwise the message for the user. }
function RunRuntimeInstaller: String;
var
  ResultCode: Integer;
begin
  Result := '';
  { "runas": the only step that needs admin rights, so the only UAC prompt. }
  if not ShellExec('runas', ExpandConstant('{tmp}\{#RuntimeFile}'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 1223 then
      Result := CustomMessage('RuntimeCancelled')
    else
      Result := FmtMessage(CustomMessage('RuntimeFailed'), [SysErrorMessage(ResultCode)]);
  end
  { 3010: installed, Windows wants a restart (the app works anyway); 1638: a newer version is already there. }
  else if ((ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1638)) or not RuntimeInstalled then
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ResultCode)]);
end;

{ After "Install" is clicked: download with its progress bar, then install. On an error the wizard stays on the Ready
  page, so it can be tried again. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if (CurPageID <> wpReady) or RuntimeInstalled then
    Exit;
  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', '{#RuntimeFile}', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox(FmtMessage(CustomMessage('RuntimeDownloadFailed'), [GetExceptionMessage]), mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
  if not Result then
    Exit;

  RuntimePage.Show;
  try
    Error := RunRuntimeInstaller;
  finally
    RuntimePage.Hide;
  end;
  if Error <> '' then
  begin
    SuppressibleMsgBox(Error, mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp;
  Result := '';
  if RuntimeInstalled then
    Exit;
  { Only for silent setups, which skip the Ready page: the same steps, without pages. }
  try
    DownloadTemporaryFile('{#RuntimeUrl}', '{#RuntimeFile}', '', nil);
  except
    Result := FmtMessage(CustomMessage('RuntimeDownloadFailed'), [GetExceptionMessage]);
    Exit;
  end;
  Result := RunRuntimeInstaller;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, LegacyDataDir: String;
begin
  if CurUninstallStep = usUninstall then
    CloseRunningApp
  else if CurUninstallStep = usPostUninstall then
  begin
    { Where the app keeps settings.json and the Workshop cache (AppSettings.DataDirectory), and where earlier
      builds kept them (AppSettings.LegacyDataDirectory). Kept on a silent uninstall. }
    DataDir := ExpandConstant('{%USERPROFILE}\PZModInspector');
    LegacyDataDir := ExpandConstant('{localappdata}\PZModInspector');
    if UninstallSilent or not (DirExists(DataDir) or DirExists(LegacyDataDir)) then
      Exit;
    if MsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(DataDir, True, True, True);
      DelTree(LegacyDataDir, True, True, True);
    end;
  end;
end;
