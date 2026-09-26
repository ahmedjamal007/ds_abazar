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

#define AppName "Dawaii"
#define AppVersion "2.8"
#define ExeName "Dawaii.exe"
#define AppBin "..\src\Dawaii.App\bin\Release\net48"
#define DbDir "..\db"

; The Telegram bot: a SEPARATE Windows Service, not part of the program. The till gets closed at the
; end of a shift and the owner still wants to ask about stock from their phone.
;
; Published SELF-CONTAINED: this is a net10.0 worker and the program itself is still .NET Framework
; 4.8, so a pharmacy PC has no .NET 10 runtime on it and generally no internet to fetch one. A
; framework-dependent build would install cleanly and then fail at service start, which is the worst
; of both — it looks installed and answers nobody.
;
;   dotnet publish ..\src\Erp.TelegramBot -c Release -r win-x64 --self-contained true -o ..\dist\bot
#define BotBin "..\dist\bot"
#define BotService "DawaiiTelegramBot"

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

; The bot, only when it was asked for. appsettings.json is deliberately EXCLUDED and written by this
; installer instead: the published copy is a developer's file, and if one ever had a live token left
; in it, that token would ship to every pharmacy — and a leaked bot token can only be stopped by
; revoking it in BotFather. Writing it here also sets ErpDirectory, which nothing else can know.
Source: "{#BotBin}\*"; DestDir: "{app}\bot"; Excludes: "appsettings.json"; \
  Flags: recursesubdirs ignoreversion; Check: InstallingBot

[UninstallDelete]
; Written by this installer rather than copied, so uninstall does not know about it.
Type: filesandordirs; Name: "{app}\bot"

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
  BotPage: TInputOptionWizardPage;     { install the Telegram bot service on this PC? }
  BotPageSeen: Boolean;                { so revisiting the page does not undo a deliberate 'no' }

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

  BotPage := CreateInputOptionPage(CounterPage.ID,
    'مساعد تيليجرام', 'الاطّلاع على تقارير الصيدلية من الهاتف',
    'يُثبّت كخدمة تعمل في الخلفية وتبدأ مع الجهاز، فيعمل المساعد حتى والبرنامج مغلق.' + #13#10 +
    'للاطّلاع فقط — لا يُعدّل أي بيانات. ولا يعمل حتى يُدخل المدير رمز البوت من داخل البرنامج.',
    True, False);
  BotPage.Add('نعم — تثبيت مساعد تيليجرام على هذا الجهاز');
  BotPage.Add('لا — بدون مساعد تيليجرام');
  { Off unless asked for: it is a background service, not a convenience. Turned ON in
    CurPageChanged when this PC already has one, which cannot be decided until the install
    directory is settled. }
  BotPage.SelectedValueIndex := 1;
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
  { A counter is a till. The owner's phone should reach the manager's PC, and two bots polling
    the same Telegram account would fight over every message. }
  if PageID = BotPage.ID then Result := Mode = 2;
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

{ True when the wizard asked for the bot. Also the [Files] Check, so nothing is copied otherwise. }
function InstallingBot: Boolean;
begin
  Result := (Mode <> 2) and (BotPage.SelectedValueIndex = 0);
end;

function BotDir: string;
begin
  Result := ExpandConstant('{app}\bot');
end;

{ Runs sc.exe and hands back its exit code. -1 means it could not be run at all. }
function Sc(const params: string): Integer;
var
  rc: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), params, '', SW_HIDE, ewWaitUntilTerminated, rc) then
    Result := -1
  else
    Result := rc;
end;

function ServiceExists: Boolean;
begin
  { 1060 is "the specified service does not exist". }
  Result := Sc('query {#BotService}') <> 1060;
end;

procedure StopBotService;
begin
  if ServiceExists then
  begin
    Sc('stop {#BotService}');
    { Windows reports the stop as pending and returns immediately. Without this the executable is
      still locked when the file copy starts, and the install fails or demands a reboot. }
    Sleep(3000);
  end;
end;

{ JSON needs its backslashes doubled, and "C:\Program Files\Dawaii" has two of them. }
function JsonEscape(const path: string): string;
var
  i: Integer;
begin
  Result := '';
  for i := 1 to Length(path) do
    if path[i] = '\' then Result := Result + '\\' else Result := Result + path[i];
end;

{ Written here rather than shipped from the publish folder, for two reasons.
  The published copy is a developer's file, and a live token accidentally left in one would ship to
  every pharmacy — a leaked bot token can only be stopped by revoking it in BotFather.
  And ErpDirectory can only be known at install time: it is where dawaii.ini ends up. A bot pointed
  at the wrong database does not fail, it reports cheerfully empty stock, which is far worse.
  Rewritten on every upgrade so that directory is never left stale. }
procedure WriteBotSettings;
var
  j: string;
begin
  j :=
    '{' + #13#10 +
    '  "Logging": {' + #13#10 +
    '    "LogLevel": {' + #13#10 +
    '      "Default": "Information",' + #13#10 +
    '      "Microsoft.Hosting.Lifetime": "Information",' + #13#10 +
    '      "System.Net.Http.HttpClient": "Warning"' + #13#10 +
    '    }' + #13#10 +
    '  },' + #13#10 +
    '  "Bot": {' + #13#10 +
    '    "Token": "",' + #13#10 +
    '    "AdminTelegramUserIds": [],' + #13#10 +
    '    "PollTimeoutSeconds": 30,' + #13#10 +
    '    "RateLimitPerMinute": 20,' + #13#10 +
    '    "LinkAttemptsPerMinute": 10,' + #13#10 +
    '    "ErpDirectory": "' + JsonEscape(ExpandConstant('{app}')) + '",' + #13#10 +
    '    "BotDatabasePath": "",' + #13#10 +
    '    "OutboxPollSeconds": 5,' + #13#10 +
    '    "OutboxMaxAttempts": 12,' + #13#10 +
    '    "LowStockDigest": true,' + #13#10 +
    '    "LowStockDigestHour": 9,' + #13#10 +
    '    "LowStockDigestNames": 8' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10;

  SaveStringToFile(BotDir + '\appsettings.json', j, False);
end;

function BotNextStepMessage: string;
begin
  Result :=
    'تم تثبيت مساعد تيليجرام، ولم يبدأ بعد لأنّه لا يوجد رمز بوت.' + #13#10 + #13#10 +
    'افتح دوائي كمدير، ثم "إعداد تيليجرام":' + #13#10 +
    '١) أدخل الرمز السرّي من BotFather' + #13#10 +
    '٢) أنشئ رمز ربط، وأرسله من تيليجرام' + #13#10 + #13#10 +
    'سيعمل المساعد بعدها تلقائيّاً مع كل تشغيل للجهاز.';
end;

{ Registers the service and tries to start it.
  A fresh install has no token yet, so the start FAILS — by design: the bot refuses to run rather
  than poll forever unable to authorize anybody. That is not an install error, and the final page
  says what to do next. On an upgrade the token is already sealed in the bot's database and the
  service simply comes back up. }
procedure InstallBotService;
var
  exe: string;
begin
  exe := BotDir + '\Erp.TelegramBot.exe';
  if not FileExists(exe) then Exit;

  WriteBotSettings;

  if ServiceExists then
    Sc('config {#BotService} binPath= "' + exe + '" start= auto')
  else
    Sc('create {#BotService} binPath= "' + exe + '" start= auto DisplayName= "Dawaii Telegram Bot"');

  Sc('description {#BotService} "Dawaii pharmacy - Telegram reporting bot for the manager"');

  { Restart on failure, but slowly: a bot that cannot reach its database should not hammer the PC. }
  Sc('failure {#BotService} reset= 86400 actions= restart/60000/restart/120000/restart/300000');

  { Silent installs are scripted by whoever deploys to a chain of shops; a modal box there would
    hang the run until somebody walked over and clicked it. }
  if (Sc('start {#BotService}') <> 0) and (not WizardSilent) then
    MsgBox(BotNextStepMessage, mbInformation, MB_OK);
end;

procedure RemoveBotService;
begin
  if not ServiceExists then Exit;
  Sc('stop {#BotService}');
  Sleep(3000);
  Sc('delete {#BotService}');
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

{ The service holds its own executable open. Stop it before anything is copied over it, or the
  install fails on a locked file and asks for a reboot it does not need. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopBotService;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = BotPage.ID) and (not BotPageSeen) then
  begin
    BotPageSeen := True;
    { An upgrade must never quietly remove a bot the pharmacy is already using. Asked here rather
      than at wizard init, because the install directory is only settled once that page has
      been through. }
    if FileExists(BotDir + '\Erp.TelegramBot.exe') or ServiceExists then
      BotPage.SelectedValueIndex := 0;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteIni;
    if Mode = 0 then PrepareLocalDatabase;
    if Mode = 1 then SetupManagerDatabase;

    if InstallingBot then
      InstallBotService
    else
    begin
      { They were asked and said no. Leaving a registered service behind after that would be the
        installer deciding it knew better — and leaving the files behind would have the NEXT upgrade
        see an installed bot and default the choice back to yes. }
      RemoveBotService;
      DelTree(BotDir, True, True, True);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Before the files go, while the executable the service points at still exists. }
  if CurUninstallStep = usUninstall then RemoveBotService;
end;
