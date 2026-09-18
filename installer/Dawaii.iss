; =====================================================================
;  دوائي (Dawaii) — Inno Setup installer (V1.2)
;  Asks how this PC will be used:
;    1) كمبيوتر واحد (single)   -> local SQLite, zero setup (default)
;    2) جهاز المدير (manager)   -> shared MySQL database on this PC (network mode, req 6)
;    3) جهاز كاشير (counter)    -> connects to the manager's MySQL over the LAN
;
;  ...then what to start from:
;    1) ترقية        -> keep the data already on this PC (default when a database is found)
;    2) صيدلية جديدة -> start empty; the app seeds its demo catalogue on first run
;    3) صيدلية حالية -> import the pharmacy's backup (.db for single, .sql dump for manager).
;                       The app reshapes it to the current schema the first time it starts.
;  Nothing is ever deleted: any database already on the PC is renamed aside, not overwritten.
;  (On a manager device "صيدلية جديدة" does not erase an existing MySQL `dawaii` database — this
;   installer never drops one. Drop it by hand first if a manager PC is being reused.)
;
;  Build the app first (Release), then compile with Inno Setup 6:
;     dotnet build ..\Dawaii.sln -c Release
;     ISCC Dawaii.iss
; =====================================================================

#define AppName "دوائي Dawaii"
#define AppVersion "2.3.0"
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
Name: "{group}\دوائي Dawaii"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\دوائي Dawaii"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "إنشاء اختصار على سطح المكتب"; GroupDescription: "اختصارات:"

[Run]
Filename: "{app}\{#ExeName}"; Description: "تشغيل دوائي"; Flags: nowait postinstall skipifsilent

[Code]
const
  DataKeep = 0;    { leave this PC's database alone — just upgrade the program }
  DataNew = 1;     { new pharmacy: start from an empty database (the app seeds the demo) }
  DataImport = 2;  { existing pharmacy: bring its backup onto this PC }

var
  ModePage: TInputOptionWizardPage;
  DataPage: TInputOptionWizardPage;    { upgrade / new pharmacy / import a backup }
  ImportPage: TInputFileWizardPage;    { the backup file, when importing }
  ManagerPage: TInputQueryWizardPage;  { root pwd + app pwd }
  CounterPage: TInputQueryWizardPage;  { manager IP + app pwd }

{ Where the single-PC backend keeps its database — must match AppConfig.DatabasePath. }
function LocalDbPath: string;
begin
  Result := ExpandConstant('{commonappdata}\Dawaii\dawaii.db');
end;

procedure InitializeWizard;
begin
  ModePage := CreateInputOptionPage(wpSelectDir,
    'طريقة الاستخدام', 'كيف سيُستخدم هذا الجهاز؟',
    'اختر واحداً:', True, False);
  ModePage.Add('كمبيوتر واحد فقط (الأبسط — بدون إعداد)');
  ModePage.Add('جهاز المدير (يحتفظ بقاعدة البيانات ويشاركها على الشبكة)');
  ModePage.Add('جهاز كاشير إضافي (يتصل بجهاز المدير)');
  ModePage.SelectedValueIndex := 0;

  DataPage := CreateInputOptionPage(ModePage.ID,
    'بيانات الصيدلية', 'ما الذي تبدأ به على هذا الجهاز؟',
    'اختر واحداً:', True, False);
  DataPage.Add('ترقية — الإبقاء على البيانات الموجودة على هذا الجهاز');
  DataPage.Add('صيدلية جديدة — البدء ببيانات تجريبية للتدريب');
  DataPage.Add('صيدلية حالية — استيراد نسخة احتياطية من بياناتها');
  { Default to the safe answer: upgrade where data exists, new pharmacy on a clean PC. }
  if FileExists(LocalDbPath) then
    DataPage.SelectedValueIndex := DataKeep
  else
    DataPage.SelectedValueIndex := DataNew;

  ImportPage := CreateInputFilePage(DataPage.ID,
    'استيراد نسخة احتياطية', 'ملف بيانات الصيدلية',
    'اختر ملف النسخة الاحتياطية. البيانات الموجودة على هذا الجهاز تُحفظ بجوارها ولا تُحذف،' + #13#10 +
    'ويقوم البرنامج بتحديث النسخة المستوردة إلى الإصدار الجديد عند أول تشغيل.');
  ImportPage.Add('ملف النسخة:', 'نسخة دوائي|*.db;*.sql|كل الملفات|*.*', '.db');

  ManagerPage := CreateInputQueryPage(ImportPage.ID,
    'إعداد قاعدة بيانات المدير', 'بيانات MySQL على هذا الجهاز',
    'يجب أن يكون خادم MySQL مثبتاً ويعمل كخدمة. أدخل كلمتي المرور:');
  ManagerPage.Add('كلمة مرور root في MySQL:', True);
  ManagerPage.Add('كلمة مرور مستخدم التطبيق (dawaii_app):', True);

  CounterPage := CreateInputQueryPage(ManagerPage.ID,
    'الاتصال بجهاز المدير', 'عنوان جهاز المدير على الشبكة',
    'أدخل عنوان IP لجهاز المدير وكلمة مرور مستخدم التطبيق:');
  CounterPage.Add('عنوان IP لجهاز المدير:', False);
  CounterPage.Add('كلمة مرور مستخدم التطبيق (dawaii_app):', True);
end;

function Mode: Integer;
begin
  Result := ModePage.SelectedValueIndex; { 0 single, 1 manager, 2 counter }
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  { A counter holds no data of its own — its pharmacy lives in the manager's database. }
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
    MsgBox('لم يتم العثور على الملف المحدد.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Mode = 1 then
  begin
    { Manager device: the backup is a mysqldump, loaded by setup_mysql.ps1. }
    if Lowercase(ExtractFileExt(f)) <> '.sql' then
    begin
      MsgBox('في وضع الشبكة يجب اختيار نسخة MySQL بامتداد sql.', mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

  if not IsSqliteFile(f) then
  begin
    MsgBox('هذا الملف ليس نسخة احتياطية لقاعدة بيانات دوائي. اختر ملفاً بامتداد db.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure WriteIni;
var
  ini: string;
begin
  if Mode = 0 then
    Exit; { single computer: no ini, app defaults to local SQLite }

  ini := '# دوائي — network configuration (written by installer)' + #13#10 + 'mode=server' + #13#10;
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
    MsgBox('تعذّر نسخ ملف النسخة الاحتياطية إلى:' + #13#10 + LocalDbPath, mbError, MB_OK);
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
    MsgBox('تعذّر تشغيل إعداد MySQL. تأكد من تثبيت MySQL Server أولاً.', mbError, MB_OK)
  else if rc <> 0 then
    MsgBox('انتهى إعداد قاعدة البيانات برمز ' + IntToStr(rc) + '. راجع الرسائل.', mbInformation, MB_OK);
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
