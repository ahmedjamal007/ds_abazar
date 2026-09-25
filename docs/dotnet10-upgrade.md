# Dawaii (دوائي) — .NET Framework 4.8 → .NET 10

**Upgrade assessment · what breaks, what gets better, what it would cost**

| | |
|---|---|
| **Today** | `net48` · `LangVersion 7.3` · three projects · 511 test cases |
| **Target** | `net10.0` (Core) · `net10.0-windows` (App, Tests) · C# 14 |
| **.NET 10** | LTS · released 11 Nov 2025 · supported to **14 Nov 2028** |
| **Deadline pressure** | **None.** `net48` is a Windows OS component with no standalone end-of-support date |
| **Verdict** | Worth doing, but not for the reasons usually given. The compile is small. The danger is silent, and it is Arabic |

---

## 0. The verdict in one breath

The port itself is smaller than it looks. `Dawaii.Core` is genuinely clean — no `System.Drawing`, no
`System.Windows`, no WinForms, no `ConfigurationManager`, no `BinaryFormatter`, no remoting, no
`packages.config`, no binding redirects. Every NuGet package the solution uses has a current version that
targets `net10.0`, and I compiled and ran the exact export code against the new ones. About four files need
real edits and one `.csproj` needs three lines.

The danger is somewhere else entirely. .NET 5 and later replaced Windows' NLS globalization with ICU, and for
**Arabic cultures that changes the decimal separator from `.` to `٫` (U+066B) and the thousands separator from
`,` to `٬` (U+066C)**. Dawaii formats and parses money as strings in dozens of places without saying which
culture to use. I measured it on this machine: on `ar-SD` a `NumericUpDown` that accepts `25.50` today
**silently reverts to its previous value** after the upgrade, and `decimal.TryParse("1234.50")` — which is how
a customer's debt payment is read in `StatementForm.cs` — goes from `true` to `false`.

That is the whole risk profile in one sentence: **nothing in the money arithmetic changes, and everything at
the string boundary does.**

The performance story is real but should not be the reason. Decimal math and LINQ are two to four times
faster, which for a fifteen-line sale is the difference between microseconds and microseconds. Startup
measured *slower*, not faster. The honest reasons to do this are the three-year LTS runway, live and patched
dependencies, and seventeen years of C# the codebase is currently locked out of.

---

## 1. How this was checked

Everything below about *this* codebase comes from reading it. Everything below about behaviour was measured on
this machine, not recalled:

- The A/B probes were compiled twice — once with `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
  (real `net48`, real NLS) and once with the .NET 8 SDK (real ICU) — and run back to back.
- The candidate packages (`NPOI 2.8.1`, `PDFsharp-gdi 6.2.4`, `System.Data.SQLite.Core 1.0.119`) were
  restored and the exact call sites from `ReportExporter.cs` and `SqliteConnectionFactory.cs` were compiled
  and executed against them.

**The caveat that matters:** this machine has the .NET 8 SDK, not .NET 10. .NET 8, 9 and 10 all use ICU on
Windows, and the `ar-*` separator convention comes from CLDR, which has been stable on this point across these
releases — so the measured direction is right. But the numbers are .NET 8 numbers. Where that matters, it
says so.

---

## 2. What actually breaks

Ordered by how much it would hurt, not by how hard it is to fix.

### 2.1 The Arabic locale change — highest risk, entirely silent

.NET 5+ uses ICU rather than NLS for globalization on Windows
([breaking change notice](https://learn.microsoft.com/en-us/dotnet/core/compatibility/globalization/5.0/icu-globalization-api)).
Measured, same code, same machine:

| Probe (culture `ar-SD`) | `net48` / NLS | .NET 8 / ICU |
|---|---|---|
| `NumberDecimalSeparator` | `.` | `٫` (U+066B) |
| `NumberGroupSeparator` | `,` | `٬` (U+066C) |
| `(1234567.895m).ToString("#,##0.00")` | `1,234,567.90` | `1٬234٬567٫90` |
| `(1234567.895m).ToString("0.00")` | `1234567.90` | `1234567٫90` |
| `decimal.TryParse("1234.50")` | **`true` → 1234.50** | **`false` → 0** |
| `NumericUpDown`, user types `25.50` | **Value = 25.50** | **Value reverts to its previous value** |
| Neutral `ar` calendar | `UmAlQuraCalendar` | `GregorianCalendar` |

The `NumericUpDown` result was produced by driving the real control's own `ValidateEditText()` on both
runtimes. It is not an inference.

**What that hits, by file:**

*Number entry that stops working* — the user types `.`, the field rejects it, and nothing says so:

| File | What the user was doing |
|---|---|
| `src/Dawaii.App/Forms/StatementForm.cs:98` | Recording a customer's debt payment (`decimal.TryParse(s.Trim(), …)`) |
| `src/Dawaii.App/Forms/StatementForm.cs:107` | Adding a manual debt charge |
| `src/Dawaii.App/Modules/EmployeeAffairsModule.cs:129` | Setting a monthly salary |
| `src/Dawaii.App/Modules/EmployeeAffairsModule.cs:138` | Recording a deduction |
| `src/Dawaii.App/Forms/ExpenseForm.cs:40` | Cash amount (`NumericUpDown`, 2 dp) |
| `src/Dawaii.App/Forms/InvoicePaymentForm.cs:58` | Paying a supplier invoice (`NumericUpDown`) |
| `src/Dawaii.App/Forms/PriceEditForm.cs:28` | Box selling price (`NumericUpDown`) |
| `src/Dawaii.App/Forms/PurchaseLineForm.cs` | Purchase/selling price per box (`NumericUpDown`) |
| `src/Dawaii.App/Forms/FullStockForm.cs:275` | Box purchase/selling price (`NumericUpDown`) |

The two forms that already do the right thing are worth naming, because they are the pattern to copy:
`FullStockForm.cs:166-167` and `ReceiveStockForm.cs:156-157` both try `CurrentCulture` and then fall back to
`InvariantCulture`. Measured, that fallback keeps working on ICU. But it only rescues the *reading* of
`n.Text`; `NumericUpDown`'s own commit path still rejects the keystroke, so those two forms are less exposed,
not immune.

*Number display that silently changes shape* — `.ToString("0.00")` with no culture, roughly 40 call sites:

- `src/Dawaii.App/Modules/ShiftReportModule.cs` — the entire shift report, including **النقد المتوقع في الدرج**
- `src/Dawaii.App/Modules/ReportsModule.cs` — every daily/weekly/monthly total and every profit figure
- `src/Dawaii.App/Modules/PurchasesModule.cs`, `EmployeeAffairsModule.cs`, `PricingModule.cs`
- `src/Dawaii.App/Ui/Fmt.cs:29` — `Money()` uses `ToString("#,##0.00")`, so every KPI card and grid cell
- `src/Dawaii.Core/Printing/ReceiptContent.cs:33-39` — the **ESC/POS text receipt** money column

*Date entry* — `EmployeeAffairsModule.cs:151` reads a leave date with a bare `DateTime.TryParse`. On `ar-SA`
this already fails today (`DateTime.TryParse("2026-10-01")` returned `false` on `net48`, because `ar-SA`'s
calendar is Um al-Qura on both runtimes). On neutral `ar` the upgrade *fixes* it, because ICU makes `ar`
Gregorian. Either way the printed date changes on `ar` machines: `1448-04-14` becomes `2026-09-25`.

**What is already safe**, and this is a genuinely large part of the app:

- `src/Dawaii.Core/Data/Db.cs` — every timestamp written and read uses `CultureInfo.InvariantCulture`.
  Database round-trips are unaffected.
- `SqliteStockRepository.cs`, `SqliteEmployeeRepository.cs`, `SqliteSupplierRepository.cs` — date parameters
  are invariant.
- `src/Dawaii.App/Printing/SaleReceiptBuilder.cs:24` and `DailySalesReceiptBuilder.cs:23` both pin
  `private static readonly CultureInfo En = CultureInfo.InvariantCulture`, and
  `ReceiptImageRenderer.cs:54` passes `CultureInfo.InvariantCulture` into `FormattedText`. **The rasterised
  thermal receipt — the thing the customer walks out with — is immune.** It is the older text-mode
  `ReceiptContent.cs` path that is not.
- `PricingService.cs:56-57` reads the rounding step invariantly.
- Search and sort happen in SQL (`LIKE @t`, `ORDER BY name` in `SqliteItemRepository.cs:47-48`,
  `SqliteCustomerRepository.cs:28`, `SqliteSupplierRepository.cs:63`), not in .NET string comparison. ICU's
  collation changes therefore do not touch drug search results. The handful of .NET string comparisons that
  exist (`DatabaseInitializer.cs:367,377`, `SqliteItemCodeRepository.cs:75-77`) already pass
  `StringComparison.Ordinal*`. I re-ran the classic ICU `IndexOf`/`StartsWith` regressions on both runtimes
  and both gave identical answers.

**The mitigations, cheapest first:**

1. **Pin the culture at startup** in `Program.cs`, before `AppServices` is built. One statement setting
   `CultureInfo.DefaultThreadCurrentCulture` to a culture whose separators are `.` and `,` makes the whole
   class of problems disappear and makes behaviour identical on every pharmacy PC regardless of its Windows
   locale — which is arguably what a single-market app should have been doing since 2019. Note the app
   deliberately does *not* use `.ToString("C")`; the currency label comes from the `currency` setting via
   `Fmt.Currency`, so ICU's currency-symbol change does not reach the user.
2. **Or** set `<InvariantGlobalization>` / the NLS switch in the runtime config. Both are blunt: the first
   also flattens casing and collation, the second re-opts into an API Microsoft is moving away from.
3. **Then**, independently, make the string boundary explicit: invariant on every `ToString` and `TryParse`
   that represents money. This is the durable fix and it is mechanical.

### 2.2 The thermal printer's Arabic code page — silent, and already half-written in a comment

`src/Dawaii.Core/Printing/EscPosReceiptPrinter.cs:53-54`:

```csharp
private static Encoding GetArabicEncoding()
{
    try { return Encoding.GetEncoding(1256); }
    catch { return Encoding.UTF8; }
}
```

Measured: on `net48` this returns `windows-1256`. On modern .NET it **throws
`NotSupportedException`** — "No data is available for encoding 1256" — because .NET Core dropped the built-in
code pages. The `catch` then quietly hands back UTF-8, and the printer has already been told `ESC t 22`
(select CP1256) on line 35. The bytes for بنادول go from `C8-E4-C7-CF-E6-E1` to
`D8-A8-D9-86-D8-A7-D8-AF-D9-88-D9-84` — twice as many, and nonsense to a CP1256 print head.

Nothing throws. Nothing logs. Receipts just come out as garbage.

The comment on line 23 already says *"registered via `CodePagesEncodingProvider` on .NET"* — but nothing in
the solution calls `Encoding.RegisterProvider`. The fix is the `System.Text.Encoding.CodePages` package plus
one registration line in `Program.cs`. The `catch`-to-UTF-8 fallback should probably also become a logged
failure rather than a silent one.

Scope note: `AppServices.cs:90` only constructs `EscPosReceiptPrinter` when a printer name is configured, and
the main receipt path is now the rasterised `PrintService`. So this may be a dormant path — worth confirming
against the actual shop's settings before spending time on it, but it is a one-line fix either way.

### 2.3 WPF stops resolving from the GAC

`Dawaii.App.csproj` currently pulls WPF in with bare `<Reference Include="WindowsBase" />` etc., which only
works because `net48` resolves them from the GAC. Three files depend on it:

| File | WPF types used |
|---|---|
| `src/Dawaii.App/Printing/ReceiptDocument.cs` | `FormattedText`, `DrawingContext`, `Typeface`, `Brushes`, `Pen`, `FontFamily` |
| `src/Dawaii.App/Printing/ReceiptImageRenderer.cs` | `DrawingVisual`, `RenderTargetBitmap`, `PngBitmapEncoder` |
| `src/Dawaii.App/Printing/EscPosRaster.cs` | `BitmapSource`, `PixelFormats`, `FormatConvertedBitmap` |

On `net10.0-windows` the fix is to delete those four `<Reference>` elements and add `<UseWPF>true</UseWPF>`
alongside the existing `<UseWindowsForms>true</UseWindowsForms>`. Both in one project is supported.

The APIs themselves are fine: the code already uses the seven-argument `FormattedText` constructor with
`pixelsPerDip` (`ReceiptDocument.cs:35-42`), which is the one that survived; the six-argument overload is the
one that was removed.

**One .NET 10 breaking change lands exactly here** — *"Applications referencing both WPF and WinForms must
disambiguate `MenuItem` and `ContextMenu` types"*
([list](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0)). I grepped: the codebase uses
neither `MenuItem` nor `ContextMenu`. It does not apply. The other WinForms 10 changes (`StatusStrip`
renderer, `TreeView` checkbox truncation) also do not apply — no `StatusStrip`, and nothing catches the
`OutOfMemoryException` that `System.Drawing` now raises as `ExternalException`.

### 2.4 DPAPI moves to a package

`src/Dawaii.App/AppConfig.cs:75` and `:106` use `System.Security.Cryptography.ProtectedData` to protect the
MySQL password in `dawaii.ini`, and the csproj gets it from `<Reference Include="System.Security" />`. On
.NET 10 that reference goes away and the
[`System.Security.Cryptography.ProtectedData`](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData)
package replaces it. Same API, same `DataProtectionScope.LocalMachine`, same ciphertext — existing protected
values in `dawaii.ini` keep opening. Low risk, but it is a compile error if missed.

### 2.5 The packages, one by one

| Package | Now | Candidate | Verdict |
|---|---|---|---|
| `System.Data.SQLite.Core` | 1.0.119 | **1.0.119** (still latest on NuGet, Sep 2024) | **Tested working.** See below |
| `MySql.Data` | 8.0.33 | 26.7.0 (Jul 2026) — targets `net10.0` explicitly | Big version jump, small API surface |
| `NPOI` | 2.5.6 | 2.8.1 (Sep 2026) — targets `net10.0` | Compiles unchanged. **Licence changed** |
| `PdfSharp` | 1.50.5147 (2016) | **`PDFsharp-gdi` 6.2.4** — targets `net10.0-windows7.0` | Package renamed; code compiles |
| `QRCoder` | 1.4.3 | 1.8.0 | `PngByteQRCode` unchanged; low risk |
| `NUnit` / adapter | 3.14 / 4.6 | NUnit 3.x still fine; adapter 6.x needs .NET 8+ | Routine |

**SQLite — the one I most expected to be a problem, and is not.** I built a `net8.0-windows` project against
`System.Data.SQLite.Core` 1.0.119 and ran `SqliteConnectionFactory.cs`'s exact connection-string builder —
`Version`, `ForeignKeys`, `BusyTimeout`, `JournalMode = Wal`, `SyncMode = Normal`, `DateTimeKind = Local`,
`FailIfMissing`, `Pooling`. It opened, the native interop loaded, SQLite 3.46.1 reported in,
`PRAGMA journal_mode` came back `wal`, and a REAL money column round-tripped to `1234567.895`. The package
ships `netstandard2.0`/`2.1` assets, which `net10.0` consumes the same way `net8.0` does. **I could not verify
this on .NET 10 itself** — no .NET 10 SDK on this machine — so treat it as strong evidence, not proof, and
make it the first thing you run after retargeting.

Upstream has moved to a 2.0.x line (2.0.3.0, March 2026) which
[raised the floor to .NET Standard 2.0 / .NET Framework 4.7.2](https://system.data.sqlite.org/home/doc/trunk/www/news.md),
but the NuGet listing for `System.Data.SQLite.Core` still tops out at 1.0.119. Given 1.0.119 works, staying
put is the low-risk choice. Rewriting onto `Microsoft.Data.Sqlite` is the *high*-risk choice and should not be
bundled into this upgrade: `SqliteConnectionFactory.cs` would have to be rewritten (different connection-string
keywords, no `JournalMode`/`SyncMode`/`BusyTimeout` properties — those become `PRAGMA`s), and its comment about
the measured 3.5 ms → 0.05 ms commit cost is exactly the kind of tuning that gets silently lost in a port.

**MySQL** — 8.0.33 → 26.7.0 is a jump across Oracle's renumbering, not 18 major API generations. What the
codebase actually touches is small: `MySqlConnection`, `MySqlConnectionStringBuilder` and the properties set
in `MySqlConnectionFactory.cs:33-48` (`Server`, `Port`, `Database`, `UserID`, `Password`, `CharacterSet`,
`ConnectionTimeout`, `DefaultCommandTimeout`, `Pooling`, `AllowUserVariables`, `AllowPublicKeyRetrieval`,
`SslMode`, `ConvertZeroDateTime`), plus `BackupService.cs` reading `Server`/`Port`/`UserID`/`Password`/`Database`
back off the builder. None of those are among the documented removals, which were mostly deprecated `Server`
synonyms and X DevAPI members. **But I did not compile against 26.7.0**, and network mode is the mode where a
failure takes down the whole shop at once. Build it and connect to a real MySQL before shipping.

**NPOI — the licence, not the API.** `ReportExporter.cs`'s Excel code compiles unchanged against 2.8.1,
including `wb.Write(fs)` on line 141. (The widely-cited NPOI 2.6 change that forced `Write(stream, leaveOpen)`
did not bite — the single-argument overload is still there in 2.8.1. I only know that because I compiled it.)
Two things did change:

- NPOI 2.8.0 adopted an **Open Source Maintenance Fee** model. The build emits a hard warning:
  *"NPOI: You must accept the OSMF EULA license to use NPOI. Add `<AcceptNPOIOSMFLicense>true</AcceptNPOIOSMFLicense>`
  to your project file."* The source stays Apache-2.0; the NuGet binaries carry a EULA with a monthly fee for
  revenue-generating organisations, free below roughly $10k annual gross revenue. For a single pharmacy this is
  almost certainly the free tier — but it is now a decision someone has to make, not a default.
- NPOI now depends on **SkiaSharp** (plus BouncyCastle, SharpZipLib, MathNet.Numerics). `sheet.AutoSizeColumn(c)`
  at `ReportExporter.cs:139` goes through SkiaSharp's text measurement rather than GDI+ — I confirmed this from
  the stack trace. So Arabic column widths in the exported `.xlsx` are measured by a different engine and will
  not be pixel-identical. Cosmetic, but visible. The native SkiaSharp binary is ~11 MB, which the installer now
  has to carry.

**PdfSharp — a rename and two warnings.** The modern equivalent of the 2016 `PdfSharp 1.50.5147` is
[`PDFsharp-gdi 6.2.4`](https://www.nuget.org/packages/PDFsharp-gdi/) (MIT, targets `net10.0-windows7.0`,
Windows-only, GDI+ backed — which is what `ReportExporter.cs` wants, since it renders pages with
`System.Drawing` and embeds them as PNGs). The namespaces do **not** change: `PdfSharp.Drawing` and
`PdfSharp.Pdf` stay. I compiled `ReportExporter.PdfSections` verbatim and it built and produced a valid PDF.
The only complaints were two `CS0618` obsoletions on `page.Width = PageW` / `page.Height = PageH`
(`ReportExporter.cs:159-160`) — implicit `int → XUnit` is deprecated in favour of `XUnitPt`.

Two PDFsharp 6 changes that do **not** apply here, but would if this file ever drew text through PDFsharp:
`XFontStyle` was renamed `XFontStyleEx`, and PDFsharp 6 loads no fonts automatically — you must set
`GlobalFontSettings.FontResolver`. Dawaii draws all its text with GDI into a `Bitmap` first and hands PDFsharp
only images, so it never touches PDFsharp's font stack. That design decision, made for Arabic shaping, happens
to skip the single most painful part of a PDFsharp 6 migration.

### 2.6 Deployment — the quiet cost nobody budgets for

`installer/Dawaii.iss` has no prerequisite check. It copies `{#AppBin}\*` into `{app}` and trusts that
`net48` is there, which on any Windows 10 or 11 machine it is. **.NET 10 is not preinstalled on Windows.**
After the upgrade the installer must either bundle the runtime (self-contained publish — a much larger folder,
now plus SkiaSharp) or chain the Windows Desktop Runtime installer. For a pharmacy in Sudan on a metered or
absent connection, "download 60 MB of runtime first" is a real support problem, so self-contained is probably
the right answer despite the size.

`src/Dawaii.App/app.config` only carries `<supportedRuntime>`. It becomes dead and can go; the comment in it
about `dawaii.ini` is worth keeping somewhere.

**What I checked and found clean:** no `packages.config`, no binding redirects, no `ConfigurationManager` or
`System.Configuration`, no `BinaryFormatter`, no `[Serializable]`, no `System.Runtime.Remoting`, no
`MarshalByRefObject`, no `Thread.Abort`, no `System.Web`, no code-access security. The single `AppDomain` use
(`Program.cs:26`, `CurrentDomain.UnhandledException`) is fully supported on .NET 10. `BackupService.cs:201-212`
already sets `UseShellExecute = false` explicitly, so the `mysqldump`/`mysql` shell-out survives the
`ProcessStartInfo` default flip that catches most migrations.

---

## 3. What gets better for free

Measured on this machine, 2,000,000 iterations, `net48` vs .NET 8, best of two runs. The operations were
chosen to mirror what `SaleBuilder`, `FefoAllocator`, `BatchPricing` and the report modules actually do:

| Operation | `net48` | .NET 8 | Change |
|---|---|---|---|
| `decimal` multiply + `Round(…, 2)` — a sale line total | 177 ms | 54 ms | **3.3× faster** |
| `decimal` divide + `Round(…, 2, AwayFromZero)` — strip price from box price | 574 ms | 283 ms | **2.0× faster** |
| LINQ `Sum` over a `List<decimal>` — `sale.Subtotal` | 117 ms | 55 ms | **2.1× faster**, half the allocation |
| `OrderBy`/`ThenBy` on nullable dates — FEFO batch ordering | 161 ms | 33 ms | **4.9× faster**, 2.3× less allocation |
| `Dictionary<int, decimal>` lookups | 138 ms | 59 ms | **2.3× faster** |
| String concat + interpolation — receipt lines | 1484 ms | 739 ms | **2.0× faster**, **3.6× less allocation** |
| `decimal.ToString("0.00")` — report cells | 589 ms | 510 ms | 1.2× faster |

.NET 10 should be equal or better than these .NET 8 figures — Microsoft merged roughly 300 performance PRs for
.NET 10, with new escape analysis that can stack-allocate enumerators
([details](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)) — so read the column as
a conservative floor.

**Now the honest part.** A pharmacy sale has maybe five to twenty lines. At these rates the entire arithmetic
of a sale costs *microseconds* on both runtimes. No cashier will ever perceive the difference. Where the
2–4× might actually show up is the places that loop over thousands of rows: the shift report, the purchases
report, `InventoryService.LowStock`/expiry scans over the whole catalogue, and `ReportExporter` building a
month of rows. Those could go from "a visible pause" to "no pause" on a slow pharmacy PC. That is a genuine
but modest win, and it is worth saying plainly rather than quoting the 12× headline benchmark, which measures
`IEnumerable<int>` summation and has nothing to do with this app.

**Startup got worse, not better.** Measured process start, hello-world, framework-dependent:

| | median | min |
|---|---|---|
| `net48` | 82 ms | 68 ms |
| .NET 8 | 170 ms | 143 ms |

That is a floor comparison, not the app — Dawaii's real startup is dominated by `ApplySchemaAndSeed()`, the
login form and GDI theme construction. But the runtime's own startup tax is higher, and the app already opens
a splash/login screen on a modest machine. Publishing with `PublishReadyToRun` (and ideally a self-contained
profile, which you'll want for the installer anyway) recovers most of it. **Budget for measuring this, not for
assuming it.**

What does **not** get faster: GDI drawing (`Gfx.cs`, `Theme.cs`, `RoundedPanel.cs`, the whole custom design
system), SQLite file I/O, the WPF rasterisation in `ReceiptImageRenderer`, spooler round-trips. Those dominate
the experience of using the app, and they are unchanged.

Genuinely useful things that are not speed:

- A **modern GC** with far better handling of the app's allocation pattern, and `Task.Run`'s thread pool is
  substantially better than `net48`'s — relevant to the background backup at `Program.cs:54`.
- **Nullable reference types** and the modern analyzer set, including `CA1305`/`CA1307`
  ("specify `IFormatProvider`" / "specify `StringComparison`"), which would have flagged every single one of
  the §2.1 culture bugs at compile time. Turning those on is arguably the highest-value thing in this whole
  document.
- `dotnet test` and `dotnet publish` working properly, rather than the `net48` SDK-style halfway house.

---

## 4. What becomes available and is worth using here

The codebase is pinned at C# 7.3. That is C# 8 through 14 unavailable — nullable reference types, records,
switch expressions, pattern matching, target-typed `new`, raw string literals, collection expressions,
primary constructors, `field`. Pointed at real files:

| Feature | Where it pays here |
|---|---|
| **Nullable reference types** (C# 8) | `Dawaii.Core` has ~81 `== null` checks. `Guard.cs`, `SaleBuilder.cs`, `ReturnCalculator.cs` and every `…N` nullable reader in `Db.cs` encode nullability in comments today. This is the single biggest safety win available |
| **`switch` expressions** (C# 8) | 19 `switch` statements, plus the `foreach`-over-tuples lookups in `PaymentMethods.LabelAr`/`Normalize` and `RoleLabels.cs` |
| **Records** (C# 9) | `Models/` is 20 files of get/set DTOs. `ReportViews.cs` (`BestSellerRow`, `DeadStockRow`), `PosTypes.cs` (`CartLine`, `SaleHeader`), `InventoryViews.cs`, `SaleLineAllocation.cs` are pure value bags — records would remove the boilerplate *and* give value equality, which several tests currently hand-roll |
| **Target-typed `new`** (C# 9) | Everywhere. `Dawaii.App/Forms/*` build controls with `new Xxx { … }` object initialisers by the hundred |
| **Primary constructors** (C# 12) | Every repository in `Data/` and every service in `Services/` is "field + constructor that assigns it". `MySqlConnectionFactory`, `SqliteConnectionFactory`, `PrintService`, `DebtService`, `PosService` all shrink |
| **Raw string literals** (C# 11) | The repositories are full of SQL built by `+`-concatenating quoted fragments — `SqliteItemRepository.cs:47-48`, `SqliteSupplierRepository.cs:120`. Multi-line raw strings make that SQL readable and diffable |
| **Collection expressions** (C# 12) | `PaymentMethods.All`, the `(string, object)[]` parameter arrays threaded through `DbExec`, the `ReportSection` row arrays in the report modules |
| **Pattern matching / list patterns** | `SaleNumberParser.cs`, `PasswordHasher.cs:37` (splitting and validating the stored hash parts) |
| **`field` keyword** (C# 14) | `Fmt.cs`'s `Currency` lazy-cache property, `AppConfig.DatabasePath`'s `_dbPath` backing field |

Runtime APIs worth adopting, in rough order of value here:

- **`DateOnly`** — `StockBatch.ExpiryDate` is a `DateTime?` that is only ever a date, and the codebase proves it
  by writing `ToString("yyyy-MM-dd")` in three repositories and comparing against a `cutoff` in
  `SqliteStockRepository.cs:49`. `DateOnly` removes a whole class of "the time component made the comparison
  wrong" bug from the near-expiry logic. This is the best API fit in the entire upgrade.
- **`ArgumentNullException.ThrowIfNull`** / `ArgumentOutOfRangeException.ThrowIf*` — `Guard.cs`,
  `SaleReceiptBuilder.cs:27`, the constructor validation in both connection factories.
- **`TimeProvider`** — the services read `DateTime.Now` directly, which is why date-sensitive tests
  (`ExpiryEvaluatorTests`, `ShiftReconciliationTests`, `DailySalesReportTests`) have to work around the clock.
- **`System.Text.Json`** — if `PurchaseDrafts` ever needs to survive a restart.
- `Random.Shared`, `CollectionsMarshal`, span-based formatting — marginal here.

One deliberate non-recommendation: **do not touch the money types.** `decimal` semantics are identical (see
below), the `decimal.Round(…, 2)` convention is consistent across the domain, and `BatchPricing` already makes
its `MidpointRounding.AwayFromZero` choices explicit. Leave it alone.

---

## 5. Pharmacy-specific risk: what could silently change a number

This is the section that matters. Ranked by "would the pharmacist notice".

**Money arithmetic: no change. Verified.** `decimal` is an IEEE 754-2008 decimal type with fixed semantics;
the .NET Core rewrite made it faster, not different. Measured identical on both runtimes:

| | `net48` | .NET 8 |
|---|---|---|
| `decimal.Round(2.345m, 2)` | 2.34 | 2.34 |
| `decimal.Round(2.345m, 2, AwayFromZero)` | 2.35 | 2.35 |
| `decimal.Round(0.125m, 2)` | 0.12 | 0.12 |
| `(double)1234567.895m` → `Convert.ToDecimal` → `Round(…, 2)` | 1234567.90 | 1234567.90 |

That last row matters because `Db.cs:17` stores money as `(double)` in SQLite's REAL columns and `Db.cs:29`
rounds it back on read. That double round-trip is a pre-existing design decision (documented, deliberate, so
`SUM()` works in SQL) and the upgrade does not perturb it. Note in passing that .NET Core 3.0 *fixed* `double`
formatting to be genuinely round-trippable, so if anything the MySQL text-protocol path gets marginally more
precise, not less.

**The one arithmetic-adjacent thing to watch** is `ReportExporter.cs:131`:

```csharp
if (decimal.TryParse(row[c], out decimal num))
    r.CreateCell(c).SetCellValue((double)num);   // numbers stay numeric for SUM
```

Report rows arrive as strings already formatted by `ToString("0.00")`, and this parses them back to write a
numeric Excel cell. Format and parse both use `CurrentCulture`, so they stay consistent with each other — I
verified the round-trip survives on ICU (`1234٫50` → `1234.50`). **But the symmetry is accidental.** If §2.1 is
fixed by making the *formatters* invariant and the parse is missed, or vice versa, every numeric cell in every
exported report silently becomes text — and the owner's `SUM()` in Excel silently returns zero. Change both
sides in the same commit.

**Printing.** Two independent failure modes, both silent, both covered above: CP1256 falling back to UTF-8
(§2.2) and `ReceiptContent.cs` money separators (§2.1). Neither throws. The rasterised receipt path is safe.
The A4 invoice path (`InvoicePrinter.cs`, `ReportPrinter.cs`, `PurchaseInvoicePrinter.cs`) is pure GDI and
unaffected — but it consumes strings formatted by the modules, so it inherits §2.1.

**Stock.** FEFO ordering is `OrderBy` on `DateTime?` and `int` (`FefoAllocator.cs:29-31`) — no culture, no
collation, no change. The `quantity >= n` guard and the transactional sale are untouched.

**The test suite will not catch any of this.** 511 test cases, and not one of them sets a `CultureInfo`.
They run under the development machine's culture — `en-US` on this box — which is precisely the culture where
every problem in §2.1 disappears. A green test run after the retarget would mean nothing about the Arabic
machine in the shop. **Before doing anything else, add a culture-parameterised fixture** that runs the money
formatting, the money parsing and the receipt builders under `ar-SD` and `ar-SA` as well as `en-US`. Run it on
`net48` first — it should pass — then let it fail on .NET 10 and fix until it passes there too. That single
test file converts this entire category from "hope" to "checked".

**Then check what the shop actually runs.** Everything in §2.1 is conditional on the pharmacy PC's Windows
locale being Arabic. If it is English, none of it fires — and if it is English today it might not be after the
next reinstall. On the shop's machine:

```
powershell -c "[System.Globalization.CultureInfo]::CurrentCulture.Name"
```

---

## 6. Suggested order of work

| # | Step | Risk | Why here |
|---|---|---|---|
| 1 | **Add the culture-parameterised test fixture** (`ar-SD`, `ar-SA`, `en-US`) over money format/parse and the receipt builders. Get it green on `net48` | **Lowest** — no production code changes | This is the instrument. Without it every later step is unverifiable |
| 2 | Check the shop PC's actual `CurrentCulture` | None | Decides how loud §2.1 is |
| 3 | On `net48`, **make the money string boundary explicit** — invariant on the `ToString`/`TryParse` pairs in §2.1, both sides together. Ship it as a normal release | Low — behaviour-preserving under `en-US`, and step 1 proves it | Decouples the risky change from the risky port. If it breaks something, you know why |
| 4 | Retarget `Dawaii.Core` to `net10.0`, `LangVersion latest`. Swap nothing else | Low — no UI, no drawing, packages unchanged | Smallest possible first port. Proves `System.Data.SQLite.Core` and `MySql.Data` load |
| 5 | Run the SQLite integration tests against a real file. Then network mode against a real MySQL | Low, high information | The two things I could not fully verify from here |
| 6 | Retarget `Dawaii.Tests` to `net10.0-windows`, update the adapter | Low | Restores the safety net before touching the UI |
| 7 | Retarget `Dawaii.App` to `net10.0-windows`: add `<UseWPF>`, drop the four GAC `<Reference>`s and `<Reference Include="System.Security" />`, add the `ProtectedData` and `Encoding.CodePages` packages, register the code-page provider in `Program.cs`, pin the culture | Medium — the compile errors are loud, the WPF/DPI behaviour is not | Everything here fails visibly except DPI |
| 8 | Upgrade `PdfSharp 1.50` → `PDFsharp-gdi 6.2.4` and `NPOI 2.5.6` → `2.8.1`. Accept or reject the NPOI licence term explicitly | Medium — code compiles, output differs | Compare an exported `.xlsx` and `.pdf` byte-for-byte against one produced by the old build |
| 9 | `MySql.Data` 8.0.33 → 26.7.0, tested against a live server | **Highest remaining** — untested here, and it fails for the whole shop at once | Do it last and do it on real hardware |
| 10 | **Print on the actual thermal printer.** Rasterised receipt, ESC/POS text receipt, A4 invoice, QR label | **Highest overall** | The only failure mode that is completely silent. No test replaces the paper |
| 11 | Rework the installer: self-contained or runtime-chaining publish, plus `PublishReadyToRun`; measure startup on the shop's PC | Medium, and it is *support* risk, not code risk | A pharmacy that cannot install the update has a worse problem than a slow one |
| 12 | Only then: nullable reference types, records, primary constructors, `DateOnly` | Low, incremental | Payoff, not migration. Turn on `CA1305`/`CA1307` first and let them find anything §2.1 missed |

Riskiest steps: **10** (silent printer failure), **9** (untested MySQL jump, blast radius = whole shop),
**7** (DPI and layout regressions in a hand-built RTL GDI design system that no test covers).
Least risky: **1**, **2**, **4** — and step 4 is the one that answers most of the open questions.

---

## 7. What I could not determine

Stated plainly, because a wrong "this works" is worse than an admission:

- **Nothing was run on .NET 10 itself.** This machine has the .NET 8 SDK only. Every measured number above is
  `net48` vs .NET 8. .NET 8, 9 and 10 share the ICU stack and the CLDR `ar-*` data, and .NET 10's runtime is
  a superset of .NET 8's performance work, so the direction of every finding holds — but the exact figures are
  .NET 8 figures.
- **`System.Data.SQLite.Core` 1.0.119 on `net10.0` specifically.** Verified working on `net8.0-windows`
  through the app's own connection-string configuration. The package's `netstandard2.0` asset should resolve
  identically on `net10.0`, but that is reasoning, not a test result. Verify at step 4.
- **`MySql.Data` 26.7.0 against this code.** Not compiled, not connected. The properties used in
  `MySqlConnectionFactory.cs` are not on any removal list I found, but I have no evidence beyond that.
  Network mode is untested end to end.
- **`NPOI 2.8.1`'s `AutoSizeColumn` output for Arabic.** Confirmed it routes through SkiaSharp; did not
  compare the resulting column widths against NPOI 2.5.6's GDI-based widths. Expect them to differ.
- **The NPOI maintenance-fee tier that applies to this business.** The threshold is roughly $10k annual gross
  revenue for the free tier; whether Dawaii's vendor crosses it is not something I can assess.
- **WinForms DPI behaviour after the port.** This app builds its entire RTL UI by hand in code — `Gfx.cs`,
  `Theme.cs`, `RoundedPanel.cs`, `KpiCard.cs`, `SideNavItem.cs`, with hard-coded `Location`/`Size` in points
  throughout the forms. .NET's WinForms defaults to a different DPI awareness mode than `net48`, and that is a
  known migration pain point. I have no way to predict how a hand-laid-out RTL form behaves at the shop's
  actual scaling setting. It has to be looked at on screen.
- **Whether `EscPosReceiptPrinter` is a live path** in the shop's current configuration, or a dormant fallback
  superseded by the rasterised `PrintService`. That depends on the `receipt_printer` setting in the running
  database.

---

## Sources

- [.NET and .NET Core official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) — .NET 10 LTS, 11 Nov 2025 → 14 Nov 2028
- [.NET Framework official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-framework) — 4.8 follows the Windows lifecycle, no standalone EOL
- [Breaking change: Globalization APIs use ICU libraries on Windows](https://learn.microsoft.com/en-us/dotnet/core/compatibility/globalization/5.0/icu-globalization-api)
- [Breaking changes in .NET 10](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0) — Windows Forms and WPF sections
- [Overview of upgrading Windows Forms apps](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/migration/)
- [Performance Improvements in .NET 10](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
- [Upgrade existing projects to PDFsharp 6](https://docs.pdfsharp.net/General/Overview/Upgrade-to-PDFsharp-6.html) · [PDFsharp NuGet package variants](https://docs.pdfsharp.net/General/Overview/NuGet-Packages.html) · [PDFsharp-gdi 6.2.4](https://www.nuget.org/packages/PDFsharp-gdi/)
- [NPOI 2.8.1 on NuGet](https://www.nuget.org/packages/npoi/) · [NPOI 2.8.0 release notes](https://github.com/nissl-lab/npoi/discussions/1751) · [Open Source Maintenance Fee licence](https://github.com/nissl-lab/npoi/issues/1785)
- [MySql.Data on NuGet](https://www.nuget.org/packages/MySql.Data) — 26.7.0, targets `net10.0`
- [System.Data.SQLite.Core 1.0.119 on NuGet](https://www.nuget.org/packages/system.data.sqlite.core/) · [System.Data.SQLite version history](https://system.data.sqlite.org/home/doc/trunk/www/news.md)
- [QRCoder on NuGet](https://www.nuget.org/packages/QRCoder) · [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData)
- [What's new in C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14)

---

Dawaii (دوائي) · upgrade assessment · research only — no code was changed to produce this document
