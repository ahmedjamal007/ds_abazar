; =====================================================================
;  Ø¯ÙˆØ§Ø¦ÙŠ (Dawaii) â€” Inno Setup installer (V1.2)
;  Asks how this PC will be used:
;    1) ÙƒÙ…Ø¨ÙŠÙˆØªØ± ÙˆØ§Ø­Ø¯ (single)   -> local SQLite, zero setup (default)
;    2) Ø¬Ù‡Ø§Ø² Ø§Ù„Ù…Ø¯ÙŠØ± (manager)   -> shared MySQL database on this PC (network mode, req 6)
;    3) Ø¬Ù‡Ø§Ø² ÙƒØ§Ø´ÙŠØ± (counter)    -> connects to the manager's MySQL over the LAN
;
;  ...then what to start from:
;    1) ØªØ±Ù‚ÙŠØ©        -> keep the data already on this PC (default when a database is found)
;    2) ØµÙŠØ¯Ù„ÙŠØ© Ø¬Ø¯ÙŠØ¯Ø© -> start empty; the app seeds its demo catalogue on first run
;    3) ØµÙŠØ¯Ù„ÙŠØ© Ø­Ø§Ù„ÙŠØ© -> import the pharmacy's backup (.db for single, .sql dump for manager).
;                       The app reshapes it to the current schema the first time it starts.
;  Nothing is ever deleted: any database already on the PC is renamed aside, not overwritten.
;  (On a manager device "ØµÙŠØ¯Ù„ÙŠØ© Ø¬Ø¯ÙŠØ¯Ø©" does not erase an existing MySQL `dawaii` database â€” this
;   installer never drops one. Drop it by hand first if a manager PC is being reused.)
;
;  Build the app first (Release), then compile with Inno Setup 6:
;     dotnet build ..\Dawaii.sln -c Release
;     ISCC Dawaii.iss
; =====================================================================

#define AppName "Dawaii"
#define AppVersion "2.6"
#define ExeName "Dawaii.exe"
#define AppBin "..\src\Dawaii.App\bin\Release\net48"
#define DbDir "..\db"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Dawaii
DefaultDirName={autopf}\Dawaii
DefaultGroupName=Dawaii
DisableProgramGroupPage=yes
OutputBaseFilename=Dawaii-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\assets\dawaii.ico
UninstallDisplayIcon={app}\{#ExeName}
PrivilegesRequired=admin

[Languages]
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[InstallDelete]
; Remove leftovers from older installs that would confuse the app.
Type: files; Name: "{app}\dawaii.ini"
Type: filesandordirs; Name: "{app}\install"

[Files]
Source: "{#AppBin}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; MySQL DB/user bootstrap (used only for the manager device).
Source: "setup_mysql.ps1"; DestDir: "{app}\install"; Flags: ignoreversion
Source: "{#DbDir}\schema.mysql.sql"; DestDir: "{app}\install"; Flags: ignoreversion
Source: "{#DbDir}\seed.mysql.sql"; DestDir: "{app}\install"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\Dawaii"; Permissions: users-modify

[Icons]
Name: "{group}\Ø¯ÙˆØ§Ø¦ÙŠ Dawaii"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\Ø¯ÙˆØ§Ø¦ÙŠ Dawaii"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Ø¥Ù†Ø´Ø§Ø¡ Ø§Ø®ØªØµØ§Ø± Ø¹Ù„Ù‰ Ø³Ø·Ø­ Ø§Ù„Ù…ÙƒØªØ¨"; GroupDescription: "Ø§Ø®ØªØµØ§Ø±Ø§Øª:"

[Run]
Filename: "{app}\{#ExeName}"; Description: "ØªØ´ØºÙŠÙ„ Ø¯ÙˆØ§Ø¦ÙŠ"; Flags: nowait postinstall skipifsilent

[Code]
const
  DataKeep = 0;    { leave this PC's database alone â€” just upgrade the program }
  DataNew = 1;     { new pharmacy: start from an empty database (the app seeds the demo) }
  DataImport = 2;  { existing pharmacy: bring its backup onto this PC }

var
  ModePage: TInputOptionWizardPage;
  DataPage: TInputOptionWizardPage;    { upgrade / new pharmacy / import a backup }
  ImportPage: TInputFileWizardPage;    { the backup file, when importing }
  ManagerPage: TInputQueryWizardPage;  { root pwd + app pwd }
  CounterPage: TInputQueryWizardPage;  { manager IP + app pwd }

{ Where the single-PC backend keeps its database â€” must match AppConfig.DatabasePath. }
function LocalDbPath: string;
begin
  Result := ExpandConstant('{commonappdata}\Dawaii\dawaii.db');
end;

procedure InitializeWizard;
begin
  ModePage := CreateInputOptionPage(wpSelectDir,
    'Ø·Ø±ÙŠÙ‚Ø© Ø§Ù„Ø§Ø³ØªØ®Ø¯Ø§Ù…', 'ÙƒÙŠÙ Ø³ÙŠÙØ³ØªØ®Ø¯Ù… Ù‡Ø°Ø§ Ø§Ù„Ø¬Ù‡Ø§Ø²ØŸ',
    'Ø§Ø®ØªØ± ÙˆØ§Ø­Ø¯Ø§Ù‹:', True, False);
  ModePage.Add('ÙƒÙ…Ø¨ÙŠÙˆØªØ± ÙˆØ§Ø­Ø¯ ÙÙ‚Ø· (Ø§Ù„Ø£Ø¨Ø³Ø· â€” Ø¨Ø¯ÙˆÙ† Ø¥Ø¹Ø¯Ø§Ø¯)');
  ModePage.Add('Ø¬Ù‡Ø§Ø² Ø§Ù„Ù…Ø¯ÙŠØ± (ÙŠØ­ØªÙØ¸ Ø¨Ù‚Ø§Ø¹Ø¯Ø© Ø§Ù„Ø¨ÙŠØ§Ù†Ø§Øª ÙˆÙŠØ´Ø§Ø±ÙƒÙ‡Ø§ Ø¹Ù„Ù‰ Ø§Ù„Ø´Ø¨ÙƒØ©)');
  ModePage.Add('Ø¬Ù‡Ø§Ø² ÙƒØ§Ø´ÙŠØ± Ø¥Ø¶Ø§ÙÙŠ (ÙŠØªØµÙ„ Ø¨Ø¬Ù‡Ø§Ø² Ø§Ù„Ù…Ø¯ÙŠØ±)');
  ModePage.SelectedValueIndex := 0;

  DataPage := CreateInputOptionPage(ModePage.ID,
    'Ø¨ÙŠØ§Ù†Ø§Øª Ø§Ù„ØµÙŠØ¯Ù„ÙŠØ©', 'Ù…Ø§ Ø§Ù„Ø°ÙŠ ØªØ¨Ø¯Ø£ Ø¨Ù‡ Ø¹Ù„Ù‰ Ù‡Ø°Ø§ Ø§Ù„Ø¬Ù‡Ø§Ø²ØŸ',
    'Ø§Ø®ØªØ± ÙˆØ§Ø­Ø¯Ø§Ù‹:', True, False);
  DataPage.Add('ØªØ±Ù‚ÙŠØ© â€” Ø§Ù„Ø¥Ø¨Ù‚Ø§Ø¡ Ø¹Ù„Ù‰ Ø§Ù„Ø¨ÙŠØ§Ù†Ø§Øª Ø§Ù„Ù…ÙˆØ¬ÙˆØ¯Ø© Ø¹Ù„Ù‰ Ù‡Ø°Ø§ Ø§Ù„Ø¬Ù‡Ø§Ø²');
  DataPage.Add('ØµÙŠØ¯Ù„ÙŠØ© Ø¬Ø¯ÙŠØ¯Ø© â€” Ø§Ù„Ø¨Ø¯Ø¡ Ø¨Ø¨ÙŠØ§Ù†Ø§Øª ØªØ¬Ø±ÙŠØ¨ÙŠØ© Ù„Ù„ØªØ¯Ø±ÙŠØ¨');
  DataPage.Add('ØµÙŠØ¯Ù„ÙŠØ© Ø­Ø§Ù„ÙŠØ© â€” Ø§Ø³ØªÙŠØ±Ø§Ø¯ Ù†Ø³Ø®Ø© Ø§Ø­ØªÙŠØ§Ø·ÙŠØ© Ù…Ù† Ø¨ÙŠØ§Ù†Ø§ØªÙ‡Ø§');
  { Default to the safe answer: upgrade where data exists, new pharmacy on a clean PC. }
  if FileExists(LocalDbPath) then
    DataPage.SelectedValueIndex := DataKeep
  else
    DataPage.SelectedValueIndex := DataNew;

  ImportPage := CreateInputFilePage(DataPage.ID,
    'Ø§Ø³ØªÙŠØ±Ø§Ø¯ Ù†Ø³Ø®Ø© Ø§Ø­ØªÙŠØ§Ø·ÙŠØ©', 'Ù…Ù„Ù Ø¨ÙŠØ§Ù†Ø§Øª Ø§Ù„ØµÙŠØ¯Ù„ÙŠØ©',
    'Ø§Ø®ØªØ± Ù…Ù„Ù Ø§Ù„Ù†Ø³Ø®Ø© Ø§Ù„Ø§Ø­ØªÙŠØ§Ø·ÙŠØ©. Ø§Ù„Ø¨ÙŠØ§Ù†Ø§Øª Ø§Ù„Ù…ÙˆØ¬ÙˆØ¯Ø© Ø¹Ù„Ù‰ Ù‡Ø°Ø§ Ø§Ù„Ø¬Ù‡Ø§Ø² ØªÙØ­ÙØ¸ Ø¨Ø¬ÙˆØ§Ø±Ù‡Ø§ ÙˆÙ„Ø§ ØªÙØ­Ø°ÙØŒ' + #13#10 +
    'ÙˆÙŠÙ‚ÙˆÙ… Ø§Ù„Ø¨Ø±Ù†Ø§Ù…Ø¬ Ø¨ØªØ­Ø¯ÙŠØ« Ø§Ù„Ù†Ø³Ø®Ø© Ø§Ù„Ù…Ø³ØªÙˆØ±Ø¯Ø© Ø¥Ù„Ù‰ Ø§Ù„Ø¥ØµØ¯Ø§Ø± Ø§Ù„Ø¬Ø¯ÙŠØ¯ Ø¹Ù†Ø¯ Ø£ÙˆÙ„ ØªØ´ØºÙŠÙ„.');
  ImportPage.Add('Ù…Ù„Ù Ø§Ù„Ù†Ø³Ø®Ø©:', 'Ù†Ø³Ø®Ø© Ø¯ÙˆØ§Ø¦ÙŠ|*.db;*.sql|ÙƒÙ„ Ø§Ù„Ù…Ù„ÙØ§Øª|*.*', '.db');

  ManagerPage := CreateInputQueryPage(ImportPage.ID,
    'Ø¥Ø¹Ø¯Ø§Ø¯ Ù‚Ø§Ø¹Ø¯Ø© Ø¨ÙŠØ§Ù†Ø§Øª Ø§Ù„Ù…Ø¯ÙŠØ±', 'Ø¨ÙŠØ§Ù†Ø§Øª MySQL Ø¹Ù„Ù‰ Ù‡Ø°Ø§ Ø§Ù„Ø¬Ù‡Ø§Ø²',
    'ÙŠØ¬Ø¨ Ø£Ù† ÙŠÙƒÙˆÙ† Ø®Ø§Ø¯Ù… MySQL Ù…Ø«Ø¨ØªØ§Ù‹ ÙˆÙŠØ¹Ù…Ù„ ÙƒØ®Ø¯Ù…Ø©. Ø£Ø¯Ø®Ù„ ÙƒÙ„Ù…ØªÙŠ Ø§Ù„Ù…Ø±ÙˆØ±:');
  ManagerPage.Add('ÙƒÙ„Ù…Ø© Ù…Ø±ÙˆØ± root ÙÙŠ MySQL:', True);
  ManagerPage.Add('ÙƒÙ„Ù…Ø© Ù…Ø±ÙˆØ± Ù…Ø³ØªØ®Ø¯Ù… Ø§Ù„ØªØ·Ø¨ÙŠÙ‚ (dawaii_app):', True);

  CounterPage := CreateInputQueryPage(ManagerPage.ID,
    'Ø§Ù„Ø§ØªØµØ§Ù„ Ø¨Ø¬Ù‡Ø§Ø² Ø§Ù„Ù…Ø¯ÙŠØ±', 'Ø¹Ù†ÙˆØ§Ù† Ø¬Ù‡Ø§Ø² Ø§Ù„Ù…Ø¯ÙŠØ± Ø¹Ù„Ù‰ Ø§Ù„Ø´Ø¨ÙƒØ©',
    'Ø£Ø¯Ø®Ù„ Ø¹Ù†ÙˆØ§Ù† IP Ù„Ø¬Ù‡Ø§Ø² Ø§Ù„Ù…Ø¯ÙŠØ± ÙˆÙƒÙ„Ù…Ø© Ù…Ø±ÙˆØ± Ù…Ø³ØªØ®Ø¯Ù… Ø§Ù„ØªØ·Ø¨ÙŠÙ‚:');
  CounterPage.Add('Ø¹Ù†ÙˆØ§Ù† IP Ù„Ø¬Ù‡Ø§Ø² Ø§Ù„Ù…Ø¯ÙŠØ±:', False);
  CounterPage.Add('ÙƒÙ„Ù…Ø© Ù…Ø±ÙˆØ± Ù…Ø³ØªØ®Ø¯Ù… Ø§Ù„ØªØ·Ø¨ÙŠÙ‚ (dawaii_app):', True);
end;

function Mode: Integer;
begin
  Result := ModePage.SelectedValueIndex; { 0 single, 1 manager, 2 counter }
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  { A counter holds no data of its own â€” its pharmacy lives in the manager's database. }
  if PageID = DataPage.ID then Result := Mode = 2;
  if PageID = ImportPage.ID then
    Result := (Mode = 2) or (DataPage.SelectedValueIndex <> DataImport);
  if PageID = ManagerPage.ID then Result := Mode <> 1;
  if PageID = CounterPage.ID then Result := Mode <> 2;
end;

{ Every SQLite database begins with this string, so a wrong file is caught here rather than by a
  confusing error the first time the pharmacy opens the program. Reading it costs a full load of the
  file, so a pharmacy whose database has grown past a few hundred MB is taken at its word. }
function IsSqliteFile(const path: string): Boolean;
var
  head: AnsiString;
  size: Integer;
begin
  if FileSize(path, size) and (size > 300000000) then
  begin
    Result := True;
    Exit;
  end;
  Result := LoadStringFromFile(path, head) and (Copy(head, 1, 15) = 'SQLite format 3');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  f: string;
begin
  Result := True;
  if CurPageID <> ImportPage.ID then Exit;

  f := Trim(ImportPage.Values[0]);
  if not FileExists(f) then
  begin
    MsgBox('Ù„Ù… ÙŠØªÙ… Ø§Ù„Ø¹Ø«ÙˆØ± Ø¹Ù„Ù‰ Ø§Ù„Ù…Ù„Ù Ø§Ù„Ù…Ø­Ø¯Ø¯.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Mode = 1 then
  begin
    { Manager device: the backup is a mysqldump, loaded by setup_mysql.ps1. }
    if Lowercase(ExtractFileExt(f)) <> '.sql' then
    begin
      MsgBox('ÙÙŠ ÙˆØ¶Ø¹ Ø§Ù„Ø´Ø¨ÙƒØ© ÙŠØ¬Ø¨ Ø§Ø®ØªÙŠØ§Ø± Ù†Ø³Ø®Ø© MySQL Ø¨Ø§Ù…ØªØ¯Ø§Ø¯ sql.', mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

  if not IsSqliteFile(f) then
  begin
    MsgBox('Ù‡Ø°Ø§ Ø§Ù„Ù…Ù„Ù Ù„ÙŠØ³ Ù†Ø³Ø®Ø© Ø§Ø­ØªÙŠØ§Ø·ÙŠØ© Ù„Ù‚Ø§Ø¹Ø¯Ø© Ø¨ÙŠØ§Ù†Ø§Øª Ø¯ÙˆØ§Ø¦ÙŠ. Ø§Ø®ØªØ± Ù…Ù„ÙØ§Ù‹ Ø¨Ø§Ù…ØªØ¯Ø§Ø¯ db.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure WriteIni;
var
  ini: string;
begin
  if Mode = 0 then
    Exit; { single computer: no ini, app defaults to local SQLite }

  ini := '# Ø¯ÙˆØ§Ø¦ÙŠ â€” network configuration (written by installer)' + #13#10 + 'mode=server' + #13#10;
  if Mode = 1 then
    ini := ini + 'host=localhost' + #13#10 + 'password=' + ManagerPage.Values[1] + #13#10
  else
    ini := ini + 'host=' + CounterPage.Values[0] + #13#10 + 'password=' + CounterPage.Values[1] + #13#10;

  ini := ini + 'port=3306' + #13#10 + 'database=dawaii' + #13#10 + 'user=dawaii_app' + #13#10 +
         'terminal_name=' + GetComputerNameString() + #13#10;
  SaveStringToFile(ExpandConstant('{app}\dawaii.ini'), ini, False);
end;

{ Moves the database already on this PC out of the way instead of overwriting it, so a wrong answer
  on the previous page is always recoverable: the file keeps its data under a dated name beside it. }
procedure ArchiveLocalDb(const reason: string);
var
  live: string;
begin
  live := LocalDbPath;
  if not FileExists(live) then Exit;
  RenameFile(live, live + '.' + reason + '-' + GetDateTimeString('yyyymmdd_hhnnss', #0, #0) + '.db');
end;

{ The write-ahead log belongs to the database that was just archived. Left behind, SQLite would
  replay it into whatever file takes its place and corrupt it. }
procedure DropWalFiles;
begin
  DeleteFile(LocalDbPath + '-wal');
  DeleteFile(LocalDbPath + '-shm');
end;

procedure PrepareLocalDatabase;
begin
  if DataPage.SelectedValueIndex = DataKeep then
    Exit;  { the app migrates what is already there the first time it starts }

  if DataPage.SelectedValueIndex = DataNew then
  begin
    ArchiveLocalDb('replaced');
    DropWalFiles;
    Exit;  { no database -> the app creates one and seeds the demo catalogue }
  end;

  ArchiveLocalDb('before-import');
  DropWalFiles;
  if not FileCopy(ImportPage.Values[0], LocalDbPath, False) then
    MsgBox('ØªØ¹Ø°Ù‘Ø± Ù†Ø³Ø® Ù…Ù„Ù Ø§Ù„Ù†Ø³Ø®Ø© Ø§Ù„Ø§Ø­ØªÙŠØ§Ø·ÙŠØ© Ø¥Ù„Ù‰:' + #13#10 + LocalDbPath, mbError, MB_OK);
end;

procedure SetupManagerDatabase;
var
  rc: Integer; params: string;
begin
  params := '-ExecutionPolicy Bypass -NoProfile -File "' + ExpandConstant('{app}\install\setup_mysql.ps1') + '"' +
            ' -RootPassword "' + ManagerPage.Values[0] + '"' +
            ' -AppPassword "' + ManagerPage.Values[1] + '"' +
            ' -SchemaFile "' + ExpandConstant('{app}\install\schema.mysql.sql') + '"' +
            ' -SeedFile "'   + ExpandConstant('{app}\install\seed.mysql.sql') + '"';
  if DataPage.SelectedValueIndex = DataImport then
    params := params + ' -RestoreFile "' + ImportPage.Values[0] + '"';
  if not Exec('powershell.exe', params, '', SW_SHOW, ewWaitUntilTerminated, rc) then
    MsgBox('ØªØ¹Ø°Ù‘Ø± ØªØ´ØºÙŠÙ„ Ø¥Ø¹Ø¯Ø§Ø¯ MySQL. ØªØ£ÙƒØ¯ Ù…Ù† ØªØ«Ø¨ÙŠØª MySQL Server Ø£ÙˆÙ„Ø§Ù‹.', mbError, MB_OK)
  else if rc <> 0 then
    MsgBox('Ø§Ù†ØªÙ‡Ù‰ Ø¥Ø¹Ø¯Ø§Ø¯ Ù‚Ø§Ø¹Ø¯Ø© Ø§Ù„Ø¨ÙŠØ§Ù†Ø§Øª Ø¨Ø±Ù…Ø² ' + IntToStr(rc) + '. Ø±Ø§Ø¬Ø¹ Ø§Ù„Ø±Ø³Ø§Ø¦Ù„.', mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteIni;
    if Mode = 0 then PrepareLocalDatabase;
    if Mode = 1 then SetupManagerDatabase;
  end;
end;
