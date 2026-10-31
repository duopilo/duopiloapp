; Windows-Installer (Inno Setup 6.3 oder neuer). Gebaut von installer/installer.ps1, in GitHub Actions von
; .github/workflows/release.yml:
;
;   pwsh installer/installer.ps1 -Quelle out/Duopilo -Version 0.9.1
;
; Name, Programmdatei und Version kommen als /D… von installer.ps1. Der Name steht zentral als <Product> in
; Directory.Build.props, die Programmdatei als <AssemblyName> in src/Duopilo/Duopilo.csproj.
;
; Benutzerdateien:
;  - appsettings.Local.json legt der Installer nur beim ersten Mal (leer) an und überschreibt sie nie. Normale
;    Benutzer dürfen sie bearbeiten, auch unter C:\Program Files.
;  - Mitgelieferte Dateien, die man bearbeiten kann (appsettings.json, atc-templates.json, profiles\*.json), werden bei
;    Updates erneuert. Hat jemand sie seit der letzten Installation geändert, sichert der Installer sie vorher nach
;    backup\<Datum>\. Vergleichsbasis ist shipped-files.sha256 (Prüfsummen der zuletzt installierten Fassung).
;  - %LOCALAPPDATA%\Duopilo (Logs, Modelle, Dashboard-Einstellungen) fasst der Installer nicht an.
; Deinstallation: Programm, Startmenü-Eintrag und Autostart weg; Einstellungen und Daten nur nach Rückfrage.
;
; Update von Kopilot (bis 0.9.x hieß Duopilo so – gleiche AppId):
;  - Lag Kopilot im Standardordner …\Kopilot, kommt Duopilo daneben nach …\Duopilo. appsettings.Local.json (mit
;    Schreibrecht für Benutzer), eigene Profile und Sicherungen werden übernommen, geänderte mitgelieferte Dateien
;    vorher gesichert. Danach entfernt Setup die alte Installation: Programmordner, Startmenü-Eintrag „Kopilot“;
;    der Autostart „Kopilot“ wird zu „Duopilo“. Die Deinstallation in der Systemsteuerung gilt dann für Duopilo.
;  - Lag Kopilot in einem eigenen Ordner, bleibt der Ordner; nur die alten Programmdateien Kopilot.* verschwinden,
;    Autostart und Startmenü-Eintrag werden wie oben übernommen.
;  - Den Datenordner %LOCALAPPDATA%\Kopilot übernimmt Duopilo selbst beim ersten Start.

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
  #define RepoUrl "https://github.com/duopilo/duopiloapp"
#endif
; Hauptadresse (festgelegt am 30.10.2026). duopilo.com ist nur gesichert und steht nirgends.
#ifndef Website
  #define Website "https://duopilo.app"
#endif

; Müssen zum Code passen: AppNames.Product in src/Duopilo/Hosting/AppNames.cs (Autostart-Wert in Desktop/TrayService.cs,
; Datenordner %LOCALAPPDATA%\Duopilo) – ein Test prüft, dass <Product> und AppNames.Product gleich sind.
#define AutostartName AppName
#define DataDirName AppName

; Der alte Name (AppNames.Legacy): Ordner, Programmdatei, Autostart-Wert, Startmenü-Eintrag und Datenordner bis 0.9.x.
#define LegacyName "Kopilot"
#define LegacyExe LegacyName + ".exe"

#define Repo AddBackslash(SourcePath) + ".."

[Setup]
; Bleibt für immer gleich – auch nach einer Umbenennung. Daran erkennt der Installer eine vorhandene Installation
; und aktualisiert sie, statt eine zweite anzulegen (auch eine von Kopilot).
AppId={{A9BF1AF5-AD01-4EF4-9A95-94830AA2324F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Shin_Jin_Jin
AppPublisherURL={#Website}
AppSupportURL={#Website}
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
; Neu: {autopf}\Duopilo. Update: der bisherige Ordner – außer dem alten Standardordner …\Kopilot (DefaultInstallDir).
DefaultDirName={code:DefaultInstallDir}
UsePreviousAppDir=no
DisableProgramGroupPage=yes
; Standard: für alle Benutzer nach C:\Program Files. Im Dialog (oder mit /CURRENTUSER) auch nur für den eigenen
; Benutzer ohne Adminrechte nach %LOCALAPPDATA%\Programs.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputBaseFilename={#AppName}-{#AppVersion}-Setup
SetupIconFile={#Repo}\src\Duopilo\Assets\duopilo.ico
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
english.LegacyRemoved=%1 is now called %2. Your settings, own profiles and backups were taken over to:%n%n%3%n%nThe old installation (%4) was removed. If Claude Desktop starts %1 itself, change the path in claude_desktop_config.json to %2.exe. The connector in Claude has to be connected again (the tool names changed).
english.LegacyKept=%1 is now called %2. The old program folder could not be removed completely:%n%n%3%n%nYour settings were taken over – you can delete the folder yourself.
german.AutostartGroup=Weitere Optionen:
german.AutostartTask=Mit Windows starten (Autostart)
german.StillRunning=%1 läuft noch und ließ sich nicht beenden – vermutlich startet Claude Desktop seine Instanz gleich wieder.%n%nBitte Claude Desktop über das Symbol im Infobereich beenden und Setup erneut starten.
german.StillRunningUninstall=%1 läuft noch und ließ sich nicht beenden – vermutlich startet Claude Desktop seine Instanz gleich wieder.%n%nBitte Claude Desktop über das Symbol im Infobereich beenden und die Deinstallation erneut starten.
german.BackupDone=Geänderte mitgelieferte Dateien (z. B. Profile) wurden vor dem Update gesichert:%n%n%1
german.DeleteDataQuestion=Auch die Einstellungen und Daten von %1 löschen?%n%n• im Programmordner: appsettings.Local.json, eigene Profile, Sicherungen%n• %2 (Logs, Sprachmodelle, Dashboard-Einstellungen, Claude-Anmeldungen)%n%nMit „Nein“ bleiben sie für eine spätere Installation erhalten. Ein selbst gebautes %1 nutzt denselben Datenordner.
german.LegacyRemoved=%1 heißt jetzt %2. Deine Einstellungen, eigenen Profile und Sicherungen sind übernommen nach:%n%n%3%n%nDie alte Installation (%4) wurde entfernt. Startet Claude Desktop %1 selbst, in claude_desktop_config.json den Pfad auf %2.exe ändern. Den Connector in Claude neu verbinden (die Tool-Namen haben sich geändert).
german.LegacyKept=%1 heißt jetzt %2. Der alte Programmordner ließ sich nicht ganz entfernen:%n%n%3%n%nDeine Einstellungen sind übernommen – den Ordner kannst du selbst löschen.

[Tasks]
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AutostartGroup}"; Flags: unchecked

[InstallDelete]
; Update in denselben Ordner (eigener Ordner ohne den alten Namen): Programmdateien unter dem alten Namen entfernen
Type: files; Name: "{app}\{#LegacyName}.exe"
Type: files; Name: "{app}\{#LegacyName}.dll"
Type: files; Name: "{app}\{#LegacyName}.pdb"
Type: files; Name: "{app}\{#LegacyName}.deps.json"
Type: files; Name: "{app}\{#LegacyName}.runtimeconfig.json"

[Files]
; Update von Kopilot aus einem anderen Ordner: eigene Einstellungen zuerst (mit Schreibrecht wie die leere Datei unten),
; damit die leere Vorlage danach nicht greift. LegacyDir zeigt ohne alte Installation auf einen leeren Ordner.
Source: "{code:LegacyDir}\appsettings.Local.json"; DestDir: "{app}"; Flags: external skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall; Permissions: users-modify; Check: HasLegacyDir
Source: "{#Quelle}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Manifest}"; DestDir: "{app}"; DestName: "shipped-files.sha256"; Flags: ignoreversion
; Eigene Profile und Sicherungen aus dem alten Ordner – nach den mitgelieferten, damit die neuen Profile gelten
Source: "{code:LegacyDir}\profiles\*"; DestDir: "{app}\profiles"; Flags: external skipifsourcedoesntexist onlyifdoesntexist recursesubdirs createallsubdirs uninsneveruninstall; Check: HasLegacyDir
Source: "{code:LegacyDir}\backup\*"; DestDir: "{app}\backup"; Flags: external skipifsourcedoesntexist onlyifdoesntexist recursesubdirs createallsubdirs uninsneveruninstall; Check: HasLegacyDir
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
; Update aus Duopilo heraus (still, /LAUNCH=1): danach Duopilo wieder starten
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: LaunchAfterUpdate

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ManifestName = 'shipped-files.sha256';
  { Uninstall-Schlüssel zur AppId (ohne die doppelte Klammer aus [Setup]) }
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A9BF1AF5-AD01-4EF4-9A95-94830AA2324F}_is1';

var
  BackupDir: String;
  BackupCount: Integer;
  { Ordner einer vorhandenen Kopilot-Installation, wenn Duopilo in einen anderen Ordner kommt – sonst leer.
    Wird erst in PrepareToInstall bestimmt (vorher steht der Zielordner noch nicht fest). }
  OldDir: String;

{ Update aus Duopilo heraus: Duopilo startet den Installer still mit /LAUNCH=1 und hat sich vorher beendet. }
function LaunchAfterUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:launch|0}') = '1';
end;

{ Ordner der vorhandenen Installation (gleiche AppId) laut Registry, leer = keine. }
function PreviousAppDir(): String;
var
  Path: String;
begin
  Result := '';
  if RegQueryStringValue(HKA, UninstallKey, 'Inno Setup: App Path', Path) and (Trim(Path) <> '') then
    Result := RemoveBackslashUnlessRoot(Trim(Path));
end;

{ Zielordner: neu Programme\Duopilo; Update aus dem alten Standardordner …\Kopilot daneben nach …\Duopilo;
  sonst der bisherige Ordner (Updates von Duopilo, eigene Ordner). }
function DefaultInstallDir(Param: String): String;
var
  Previous: String;
begin
  Previous := PreviousAppDir();
  if Previous = '' then
    Result := ExpandConstant('{autopf}\{#AppName}')
  else if CompareText(ExtractFileName(Previous), '{#LegacyName}') = 0 then
    Result := AddBackslash(ExtractFileDir(Previous)) + '{#AppName}'
  else
    Result := Previous;
end;

{ Liegt Child in Parent (oder ist es)? }
function IsInside(const Child, Parent: String): Boolean;
begin
  Result := Pos(Lowercase(AddBackslash(Parent)), Lowercase(AddBackslash(Child))) = 1;
end;

{ Alte Kopilot-Installation im alten Standardordner …\Kopilot, getrennt vom Zielordner? Nur dieser Ordner wird später
  entfernt – nie ein eigener Ordner (dort aktualisiert Setup an Ort und Stelle). Erst ab PrepareToInstall aufrufen. }
function FindOldDir(): String;
var
  Previous, App: String;
begin
  Result := '';
  Previous := PreviousAppDir();
  App := ExpandConstant('{app}');
  if (Previous <> '') and (CompareText(ExtractFileName(Previous), '{#LegacyName}') = 0)
     and FileExists(AddBackslash(Previous) + '{#LegacyExe}')
     and not IsInside(App, Previous) and not IsInside(Previous, App) then
    Result := Previous;
end;

{ Check für die Übernahme in [Files]. Vor PrepareToInstall (z. B. beim Berechnen des Platzbedarfs) immer False. }
function HasLegacyDir(): Boolean;
begin
  Result := OldDir <> '';
end;

{ Quelle für die Übernahme in [Files]. Ohne alte Installation ein Ordner, den es nicht gibt (skipifsourcedoesntexist). }
function LegacyDir(Param: String): String;
begin
  if HasLegacyDir() then
    Result := OldDir
  else
    Result := ExpandConstant('{tmp}\no-legacy-install');
end;

{ Beendet alle Instanzen von Duopilo und Kopilot aus den angegebenen Ordnern (auch die stdio-Instanz von Claude Desktop)
  und wartet, bis keine mehr läuft. Instanzen aus anderen Ordnern (z. B. ein selbst gebautes C:\Tools\Duopilo) bleiben
  unberührt. True, wenn am Ende keine mehr läuft. }
function StopRunningInstances(const Folders: TArrayOfString): Boolean;
var
  List, Folder, Script: String;
  I, ResultCode: Integer;
begin
  List := '';
  for I := 0 to GetArrayLength(Folders) - 1 do
  begin
    if Folders[I] <> '' then
    begin
      Folder := AddBackslash(Folders[I]);
      StringChangeEx(Folder, '''', '''''', True);
      if List <> '' then
        List := List + ', ';
      List := List + '''' + Folder + '''';
    end;
  end;
  if List = '' then
  begin
    Result := True;
    exit;
  end;
  Script :=
    '$ordner = @(' + List + '); ' +
    'for ($i = 0; $i -lt 15; $i++) { ' +
    '$p = @(Get-Process -Name ''' + ChangeFileExt('{#AppExe}', '') + ''', ''{#LegacyName}'' -ErrorAction SilentlyContinue | ' +
    'Where-Object { $pfad = $_.Path; $pfad -and ($ordner | Where-Object { $pfad.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) }); ' +
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

function StopInstancesIn(const First, Second: String): Boolean;
var
  Folders: TArrayOfString;
begin
  SetArrayLength(Folders, 2);
  Folders[0] := First;
  Folders[1] := Second;
  Result := StopRunningInstances(Folders);
end;

{ Sichert eine Datei aus Base (Programmordner der bisherigen Installation) nach backup\<Datum>\ im Zielordner. }
procedure BackupFile(const Base, RelPath: String);
var
  Target: String;
begin
  if BackupDir = '' then
    BackupDir := ExpandConstant('{app}\backup\') + GetDateTimeString('yyyy/mm/dd_hh:nn:ss', '-', '-');
  Target := BackupDir + '\' + RelPath;
  if ForceDirectories(ExtractFileDir(Target)) and FileCopy(AddBackslash(Base) + RelPath, Target, False) then
  begin
    BackupCount := BackupCount + 1;
    Log('Modified, backed up: ' + RelPath);
  end
  else
    Log('Backup failed: ' + RelPath);
end;

procedure BackupIfExists(const Base, RelPath: String);
begin
  if FileExists(AddBackslash(Base) + RelPath) then
    BackupFile(Base, RelPath);
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

{ Vor dem Kopieren: mitgelieferte Dateien sichern, die seit der letzten Installation geändert wurden. Base ist der
  Programmordner der bisherigen Installation – bei einem Update von Kopilot aus einem anderen Ordner der alte. }
procedure BackupModifiedFiles(const Base, ExeName: String);
var
  Lines: TArrayOfString;
  I, P: Integer;
  Line, RelPath: String;
  FindRec: TFindRec;
begin
  if LoadStringsFromFile(AddBackslash(Base) + ManifestName, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Line := Trim(Lines[I]);
      P := Pos('  ', Line);
      if P > 0 then
      begin
        RelPath := Trim(Copy(Line, P + 2, Length(Line)));
        if FileExists(AddBackslash(Base) + RelPath) and IsModified(AddBackslash(Base) + RelPath, Copy(Line, 1, P - 1)) then
          BackupFile(Base, RelPath);
      end;
    end;
  end
  else if FileExists(AddBackslash(Base) + ExeName) then
  begin
    { Ordner ohne Prüfsummenliste (z. B. von Hand befüllt): alles Bearbeitbare sichern, was da ist. }
    BackupIfExists(Base, 'appsettings.json');
    BackupIfExists(Base, 'atc-templates.json');
    if FindFirst(AddBackslash(Base) + 'profiles\*.json', FindRec) then
    begin
      try
        repeat
          BackupFile(Base, 'profiles\' + FindRec.Name);
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

{ Autostart-Wert, der auf eine Datei in Folder zeigt? }
function AutostartPointsInto(const ValueName, Folder: String): Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(HKCU, RunKey, ValueName, Command) and
            (Pos(Lowercase(AddBackslash(Folder)), Lowercase(Command)) > 0);
end;

{ Einträge der Kopilot-Installation in Folder übernehmen: „Mit Windows starten“ gilt jetzt für Duopilo, der alte
  Startmenü-Eintrag verschwindet (der neue heißt Duopilo). }
procedure TakeOverLegacyEntries(const Folder: String);
var
  Shortcut: String;
begin
  if AutostartPointsInto('{#LegacyName}', Folder) then
  begin
    RegDeleteValue(HKCU, RunKey, '{#LegacyName}');
    if not RegValueExists(HKCU, RunKey, '{#AutostartName}') then
      RegWriteStringValue(HKCU, RunKey, '{#AutostartName}', '"' + ExpandConstant('{app}\{#AppExe}') + '" --autostart');
    Log('Autostart moved from {#LegacyName} to {#AutostartName}.');
  end;

  Shortcut := ExpandConstant('{autoprograms}\{#LegacyName}.lnk');
  if FileExists(Shortcut) and not DeleteFile(Shortcut) then
    Log('Could not delete the old shortcut: ' + Shortcut);
end;

{ Nach dem Update von Kopilot aus dem alten Standardordner: den alten Ordner entfernen. Die Deinstallation in der
  Systemsteuerung (gleiche AppId) zeigt schon auf Duopilo. }
procedure RemoveOldFolder();
var
  App: String;
begin
  App := ExpandConstant('{app}');

  { Nur löschen, wenn die eigenen Einstellungen sicher drüben sind }
  if FileExists(AddBackslash(OldDir) + 'appsettings.Local.json') and not FileExists(App + '\appsettings.Local.json') then
  begin
    Log('appsettings.Local.json was not taken over – keeping ' + OldDir);
    SuppressibleMsgBox(FmtMessage(CustomMessage('LegacyKept'), ['{#LegacyName}', '{#AppName}', OldDir]), mbInformation, MB_OK, IDOK);
    exit;
  end;

  DelTree(OldDir, True, True, True);
  if DirExists(OldDir) then
  begin
    Log('Old folder could not be removed completely: ' + OldDir);
    SuppressibleMsgBox(FmtMessage(CustomMessage('LegacyKept'), ['{#LegacyName}', '{#AppName}', OldDir]), mbInformation, MB_OK, IDOK);
  end
  else
  begin
    Log('Removed the old installation: ' + OldDir);
    SuppressibleMsgBox(FmtMessage(CustomMessage('LegacyRemoved'), ['{#LegacyName}', '{#AppName}', App, OldDir]), mbInformation, MB_OK, IDOK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Running: String;
begin
  Result := '';
  OldDir := FindOldDir();
  if OldDir <> '' then
    Log('Update from {#LegacyName} in its old folder ' + OldDir + ' to ' + ExpandConstant('{app}'));
  if HasLegacyDir() then
    Running := '{#LegacyName}'
  else
    Running := '{#AppName}';
  if not StopInstancesIn(ExpandConstant('{app}'), OldDir) then
    Result := FmtMessage(CustomMessage('StillRunning'), [Running]);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if HasLegacyDir() then
      BackupModifiedFiles(OldDir, '{#LegacyExe}')
    else
      BackupModifiedFiles(ExpandConstant('{app}'), '{#AppExe}');
  end
  else if CurStep = ssPostInstall then
  begin
    if BackupCount > 0 then
      SuppressibleMsgBox(FmtMessage(CustomMessage('BackupDone'), [BackupDir]), mbInformation, MB_OK, IDOK);
    if HasLegacyDir() then
    begin
      TakeOverLegacyEntries(OldDir);
      RemoveOldFolder();
    end
    else
      TakeOverLegacyEntries(ExpandConstant('{app}'));
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopInstancesIn(ExpandConstant('{app}'), '');
  if not Result then
    SuppressibleMsgBox(FmtMessage(CustomMessage('StillRunningUninstall'), ['{#AppName}']), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  App, DataDir, LegacyDataDir, DataDirs: String;
begin
  App := ExpandConstant('{app}');
  if CurUninstallStep = usUninstall then
  begin
    { Autostart entfernen, wenn er auf diese Installation zeigt – auch wenn er über das Tray-Menü gesetzt wurde
      (auch ein noch alter Eintrag „Kopilot“). }
    if AutostartPointsInto('{#AutostartName}', App) then
      RegDeleteValue(HKCU, RunKey, '{#AutostartName}');
    if AutostartPointsInto('{#LegacyName}', App) then
      RegDeleteValue(HKCU, RunKey, '{#LegacyName}');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\{#DataDirName}');
    LegacyDataDir := ExpandConstant('{localappdata}\{#LegacyName}');
    DataDirs := DataDir;
    if DirExists(LegacyDataDir) then
      DataDirs := DataDirs + ', ' + LegacyDataDir;
    if SuppressibleMsgBox(FmtMessage(CustomMessage('DeleteDataQuestion'), ['{#AppName}', DataDirs]),
                          mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      DeleteFile(App + '\appsettings.Local.json');
      DelTree(App + '\profiles', True, True, True);
      DelTree(App + '\backup', True, True, True);
      DelTree(DataDir, True, True, True);
      DelTree(LegacyDataDir, True, True, True);
    end;
    RemoveDir(App);
  end;
end;
