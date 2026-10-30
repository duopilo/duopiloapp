; Windows-Installer (Inno Setup 6.3 oder neuer). Gebaut von installer/installer.ps1, in GitHub Actions von
; .github/workflows/release.yml:
;
;   pwsh installer/installer.ps1 -Quelle out/Kopilot -Version 0.9.1
;
; Name, Programmdatei und Version kommen als /D… von installer.ps1. Der Name steht zentral als <Product> in
; Directory.Build.props, die Programmdatei als <AssemblyName> in src/Kopilot/Kopilot.csproj.
;
; Benutzerdateien:
;  - appsettings.Local.json legt der Installer nur beim ersten Mal (leer) an und überschreibt sie nie. Normale
;    Benutzer dürfen sie bearbeiten, auch unter C:\Program Files.
;  - Mitgelieferte Dateien, die man bearbeiten kann (appsettings.json, atc-templates.json, profiles\*.json), werden bei
;    Updates erneuert. Hat jemand sie seit der letzten Installation geändert, sichert der Installer sie vorher nach
;    backup\<Datum>\. Vergleichsbasis ist shipped-files.sha256 (Prüfsummen der zuletzt installierten Fassung).
;  - %LOCALAPPDATA%\Kopilot (Logs, Modelle, Dashboard-Einstellungen) fasst der Installer nicht an.
; Deinstallation: Programm, Startmenü-Eintrag und Autostart weg; Einstellungen und Daten nur nach Rückfrage.

#ifndef AppName
  #error AppName is missing - please use installer/installer.ps1.
#endif
#ifndef AppExe
  #error AppExe is missing - please use installer/installer.ps1.
#endif
#ifndef AppVersion
  #error AppVersion is missing - please use installer/installer.ps1.
#endif
#ifndef AppVersionNumeric
  #error AppVersionNumeric is missing - please use installer/installer.ps1.
#endif
#ifndef Quelle
  #error Quelle is missing - please use installer/installer.ps1.
#endif
#ifndef Manifest
  #error Manifest is missing - please use installer/installer.ps1.
#endif
#ifndef RepoUrl
  #define RepoUrl "https://github.com/Shin-Jin-Jin/Kopilot"
#endif

; Müssen zum Code passen (bei der Umbenennung mitziehen): Autostart.ValueName in Desktop/TrayService.cs und der
; Datenordner %LOCALAPPDATA%\Kopilot in KopilotOptions.cs.
#define AutostartName "Kopilot"
#define DataDirName "Kopilot"

#define Repo AddBackslash(SourcePath) + ".."

[Setup]
; Bleibt für immer gleich – auch nach einer Umbenennung. Daran erkennt der Installer eine vorhandene Installation
; und aktualisiert sie, statt eine zweite anzulegen.
AppId={{A9BF1AF5-AD01-4EF4-9A95-94830AA2324F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Shin_Jin_Jin
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; Standard: für alle Benutzer nach C:\Program Files. Im Dialog (oder mit /CURRENTUSER) auch nur für den eigenen
; Benutzer ohne Adminrechte nach %LOCALAPPDATA%\Programs.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputBaseFilename={#AppName}-{#AppVersion}-Setup
SetupIconFile={#Repo}\src\Kopilot\Assets\kopilot.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes
; Laufende Instanzen beendet [Code] selbst (Restart Manager erwischt die stdio-Instanz von Claude Desktop nicht).
CloseApplications=no
RestartApplications=no
; Der Autostart (HKCU) gehört dem angemeldeten Benutzer – auch bei einer Installation mit Adminrechten so gewollt.
UsedUserAreasWarning=no

[Languages]
; Englisch zuerst: Standard und Rückfall, wenn die Windows-Anzeigesprache zu keinem Eintrag passt. Auf einem deutschen Windows
; wählt Setup Deutsch, der Sprachdialog lässt es ändern. Die Namen sind die Präfixe in [CustomMessages].
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.AutostartGroup=Additional options:
english.AutostartTask=Start with Windows (autostart)
english.StillRunning=%1 is still running and could not be stopped – Claude Desktop probably restarts its instance right away.%n%nPlease quit Claude Desktop from its notification area icon and run Setup again.
english.StillRunningUninstall=%1 is still running and could not be stopped – Claude Desktop probably restarts its instance right away.%n%nPlease quit Claude Desktop from its notification area icon and run the uninstaller again.
english.BackupDone=Modified bundled files (e.g. aircraft profiles) were backed up before the update:%n%n%1
english.DeleteDataQuestion=Also delete the settings and data of %1?%n%n• in the program folder: appsettings.Local.json, own profiles, backups%n• %2 (logs, speech models, dashboard settings, Claude sign-ins)%n%nChoose "No" to keep them for a later installation. A self-built %1 uses the same data folder.
german.AutostartGroup=Weitere Optionen:
german.AutostartTask=Mit Windows starten (Autostart)
german.StillRunning=%1 läuft noch und ließ sich nicht beenden – vermutlich startet Claude Desktop seine Instanz gleich wieder.%n%nBitte Claude Desktop über das Symbol im Infobereich beenden und Setup erneut starten.
german.StillRunningUninstall=%1 läuft noch und ließ sich nicht beenden – vermutlich startet Claude Desktop seine Instanz gleich wieder.%n%nBitte Claude Desktop über das Symbol im Infobereich beenden und die Deinstallation erneut starten.
german.BackupDone=Geänderte mitgelieferte Dateien (z. B. Profile) wurden vor dem Update gesichert:%n%n%1
german.DeleteDataQuestion=Auch die Einstellungen und Daten von %1 löschen?%n%n• im Programmordner: appsettings.Local.json, eigene Profile, Sicherungen%n• %2 (Logs, Sprachmodelle, Dashboard-Einstellungen, Claude-Anmeldungen)%n%nMit „Nein“ bleiben sie für eine spätere Installation erhalten. Ein selbst gebautes %1 nutzt denselben Datenordner.

[Tasks]
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AutostartGroup}"; Flags: unchecked

[Files]
Source: "{#Quelle}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Manifest}"; DestDir: "{app}"; DestName: "shipped-files.sha256"; Flags: ignoreversion
; Eigene Einstellungen: einmal leer anlegen, nie überschreiben, beim Deinstallieren nur nach Rückfrage löschen.
; Deutscher Installer: die neue Datei setzt "Language": "de" (nur beim ersten Mal – eine vorhandene Datei bleibt unberührt).
Source: "appsettings.Local.leer.json"; DestDir: "{app}"; DestName: "appsettings.Local.json"; Languages: english; Flags: onlyifdoesntexist uninsneveruninstall; Permissions: users-modify
Source: "appsettings.Local.leer.de.json"; DestDir: "{app}"; DestName: "appsettings.Local.json"; Languages: german; Flags: onlyifdoesntexist uninsneveruninstall; Permissions: users-modify

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; Gleicher Eintrag wie „Mit Windows starten“ im Tray-Menü – beides schaltet also dasselbe.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AutostartName}"; ValueData: """{app}\{#AppExe}"" --autostart"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ManifestName = 'shipped-files.sha256';

var
  BackupDir: String;
  BackupCount: Integer;

{ Beendet alle Instanzen aus dem Programmordner (auch die stdio-Instanz von Claude Desktop) und wartet, bis keine mehr
  läuft. Instanzen aus anderen Ordnern (z. B. ein selbst gebautes C:\Tools\Kopilot) bleiben unberührt.
  True, wenn am Ende keine mehr läuft. }
function StopRunningInstances(): Boolean;
var
  Folder, Script: String;
  ResultCode: Integer;
begin
  Folder := AddBackslash(ExpandConstant('{app}'));
  StringChangeEx(Folder, '''', '''''', True);
  Script :=
    '$ordner = ''' + Folder + '''; ' +
    'for ($i = 0; $i -lt 15; $i++) { ' +
    '$p = @(Get-Process -Name ''' + ChangeFileExt('{#AppExe}', '') + ''' -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -and $_.Path.StartsWith($ordner, [StringComparison]::OrdinalIgnoreCase) }); ' +
    'if ($p.Count -eq 0) { exit 0 }; ' +
    '$p | Stop-Process -Force -ErrorAction SilentlyContinue; ' +
    'Start-Sleep -Milliseconds 700 }; ' +
    'exit 1';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
              '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    { Ohne PowerShell weiter wie bisher – gesperrte Dateien meldet Setup dann selbst. }
    Log('Could not start PowerShell: ' + SysErrorMessage(ResultCode));
    Result := True;
    exit;
  end;
  Result := ResultCode = 0;
  if not Result then
    Log('Could not stop the running instances.');
end;

procedure BackupFile(const RelPath: String);
var
  Target: String;
begin
  if BackupDir = '' then
    BackupDir := ExpandConstant('{app}\backup\') + GetDateTimeString('yyyy/mm/dd_hh:nn:ss', '-', '-');
  Target := BackupDir + '\' + RelPath;
  if ForceDirectories(ExtractFileDir(Target)) and FileCopy(ExpandConstant('{app}\') + RelPath, Target, False) then
  begin
    BackupCount := BackupCount + 1;
    Log('Modified, backed up: ' + RelPath);
  end
  else
    Log('Backup failed: ' + RelPath);
end;

procedure BackupIfExists(const RelPath: String);
begin
  if FileExists(ExpandConstant('{app}\') + RelPath) then
    BackupFile(RelPath);
end;

function IsModified(const FullPath, ShippedHash: String): Boolean;
begin
  try
    Result := CompareText(GetSHA256OfFile(FullPath), ShippedHash) <> 0;
  except
    { Nicht lesbar: lieber sichern als verlieren. }
    Result := True;
  end;
end;

{ Vor dem Kopieren: mitgelieferte Dateien sichern, die seit der letzten Installation geändert wurden. }
procedure BackupModifiedFiles();
var
  Lines: TArrayOfString;
  I, P: Integer;
  Line, RelPath: String;
  FindRec: TFindRec;
begin
  if LoadStringsFromFile(ExpandConstant('{app}\' + ManifestName), Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Line := Trim(Lines[I]);
      P := Pos('  ', Line);
      if P > 0 then
      begin
        RelPath := Trim(Copy(Line, P + 2, Length(Line)));
        if FileExists(ExpandConstant('{app}\') + RelPath) and IsModified(ExpandConstant('{app}\') + RelPath, Copy(Line, 1, P - 1)) then
          BackupFile(RelPath);
      end;
    end;
  end
  else if FileExists(ExpandConstant('{app}\{#AppExe}')) then
  begin
    { Ordner ohne Prüfsummenliste (z. B. von Hand befüllt): alles Bearbeitbare sichern, was da ist. }
    BackupIfExists('appsettings.json');
    BackupIfExists('atc-templates.json');
    if FindFirst(ExpandConstant('{app}\profiles\*.json'), FindRec) then
    begin
      try
        repeat
          BackupFile('profiles\' + FindRec.Name);
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopRunningInstances() then
    Result := FmtMessage(CustomMessage('StillRunning'), ['{#AppName}']);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    BackupModifiedFiles()
  else if (CurStep = ssPostInstall) and (BackupCount > 0) then
    SuppressibleMsgBox(FmtMessage(CustomMessage('BackupDone'), [BackupDir]), mbInformation, MB_OK, IDOK);
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopRunningInstances();
  if not Result then
    SuppressibleMsgBox(FmtMessage(CustomMessage('StillRunningUninstall'), ['{#AppName}']), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  App, DataDir, Command: String;
begin
  App := ExpandConstant('{app}');
  if CurUninstallStep = usUninstall then
  begin
    { Autostart entfernen, wenn er auf diese Installation zeigt – auch wenn er über das Tray-Menü gesetzt wurde. }
    if RegQueryStringValue(HKCU, RunKey, '{#AutostartName}', Command) and
       (Pos(Lowercase(AddBackslash(App)), Lowercase(Command)) > 0) then
      RegDeleteValue(HKCU, RunKey, '{#AutostartName}');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\{#DataDirName}');
    if SuppressibleMsgBox(FmtMessage(CustomMessage('DeleteDataQuestion'), ['{#AppName}', DataDir]),
                          mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      DeleteFile(App + '\appsettings.Local.json');
      DelTree(App + '\profiles', True, True, True);
      DelTree(App + '\backup', True, True, True);
      DelTree(DataDir, True, True, True);
    end;
    RemoveDir(App);
  end;
end;
