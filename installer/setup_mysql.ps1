<#
  دوائي (Dawaii) — manager-device MySQL bootstrap (network mode, V1.2 req 6).
  Creates the `dawaii` database and the `dawaii_app` user (native password, works over the LAN),
  grants privileges, and applies the schema + seed. Requires MySQL Server already installed and
  running as a service (its service auto-starts at boot, so the DB is ready without opening the app).
  Safe to re-run.
#>
param(
    [Parameter(Mandatory=$true)][string]$RootPassword,
    [Parameter(Mandatory=$true)][string]$AppPassword,
    [Parameter(Mandatory=$true)][string]$SchemaFile,
    [Parameter(Mandatory=$true)][string]$SeedFile,
    # An existing pharmacy's mysqldump, loaded before the schema so the app's migrations then bring
    # it up to this version on first run. Omitted for a new pharmacy.
    [string]$RestoreFile
)
$ErrorActionPreference = "Stop"

function Find-Mysql {
    $cmd = Get-Command mysql.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $c = Get-ChildItem "C:\Program Files\MySQL","C:\Program Files (x86)\MySQL" -Recurse -Filter mysql.exe -ErrorAction SilentlyContinue
    if ($c) { return $c[0].FullName }
    throw "mysql.exe غير موجود. ثبّت MySQL Server (Community) أولاً ثم أعد تشغيل هذا الإعداد."
}

$mysql = Find-Mysql
Write-Host "MySQL client: $mysql"
$env:MYSQL_PWD = $RootPassword

# 1) Database + app user (host '%' so counters can connect) + privileges.
$bootstrap = @"
CREATE DATABASE IF NOT EXISTS dawaii CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'dawaii_app'@'%'         IDENTIFIED WITH mysql_native_password BY '$AppPassword';
ALTER  USER 'dawaii_app'@'%'         IDENTIFIED WITH mysql_native_password BY '$AppPassword';
CREATE USER IF NOT EXISTS 'dawaii_app'@'localhost' IDENTIFIED WITH mysql_native_password BY '$AppPassword';
ALTER  USER 'dawaii_app'@'localhost' IDENTIFIED WITH mysql_native_password BY '$AppPassword';
GRANT ALL PRIVILEGES ON dawaii.* TO 'dawaii_app'@'%';
GRANT ALL PRIVILEGES ON dawaii.* TO 'dawaii_app'@'localhost';
FLUSH PRIVILEGES;
"@
Write-Host "Creating database and application user..."
$bootstrap | & $mysql --user=root --default-character-set=utf8mb4
if ($LASTEXITCODE -ne 0) { throw "فشل إنشاء قاعدة البيانات/المستخدم." }

# 2) Existing pharmacy: load its dump first. Anything already in `dawaii` is kept as a dated copy of
#    the database, because a dump replaces the tables it contains.
if ($RestoreFile) {
    if (-not (Test-Path $RestoreFile)) { throw "ملف النسخة الاحتياطية غير موجود: $RestoreFile" }
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $tables = & $mysql --user=root --skip-column-names --batch -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='dawaii'"
    if ($LASTEXITCODE -eq 0 -and [int]$tables -gt 0) {
        $safety = Join-Path (Split-Path $RestoreFile -Parent) "dawaii_before_import_$stamp.sql"
        Write-Host "Saving the current database to $safety ..."
        & (Join-Path (Split-Path $mysql -Parent) "mysqldump.exe") --user=root --single-transaction --default-character-set=utf8mb4 dawaii |
            Out-File -FilePath $safety -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw "تعذّر حفظ نسخة من قاعدة البيانات الحالية قبل الاستيراد." }
    }
    Write-Host "Importing $RestoreFile ..."
    & $mysql --user=root --default-character-set=utf8mb4 dawaii -e "source $RestoreFile"
    if ($LASTEXITCODE -ne 0) { throw "فشل استيراد النسخة الاحتياطية." }
}

# 3) Apply schema + seed (idempotent). The app also self-applies these on first run.
Write-Host "Applying schema..."
& $mysql --user=root --default-character-set=utf8mb4 dawaii -e "source $SchemaFile"
if ($LASTEXITCODE -ne 0) { throw "فشل تطبيق المخطط." }
Write-Host "Applying seed..."
& $mysql --user=root --default-character-set=utf8mb4 dawaii -e "source $SeedFile"
if ($LASTEXITCODE -ne 0) { throw "فشل تطبيق البيانات الأولية." }

Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "تم إعداد قاعدة بيانات المدير بنجاح."
Write-Host "لتمكين أجهزة الكاشير من الاتصال: اضبط bind-address=0.0.0.0 في my.ini، وأعد تشغيل خدمة MySQL،"
Write-Host "واسمح بالمنفذ TCP 3306 في جدار حماية ويندوز."
exit 0
