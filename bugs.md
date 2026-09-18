# Dawaii (دوائي) — .NET Desktop Application Bug Audit

> **Phase 1 (scanner)** produced the findings. **Phase 2 (fix)** verified each one against the code
> and the test suite before touching anything. Two findings did not survive verification and are
> marked **NOT A BUG** below with the evidence — the behaviours turned out to be deliberate, documented
> and tested. One defect the scanner missed was found during the fix phase and is recorded as
> **BUG-016**.

---

## Audit Summary

| | |
|---|---|
| **Application type** | Windows desktop — Arabic (RTL) pharmacy management / POS |
| **.NET version** | .NET Framework 4.8 (`net48`), SDK-style projects |
| **C# version** | 7.3 |
| **UI framework** | WinForms (WPF referenced headlessly for DirectWrite Arabic shaping in receipts) |
| **Database** | SQLite (default) **or** MySQL (network mode) — hand-written ADO.NET, no ORM |
| **Projects** | `Dawaii.Core`, `Dawaii.App`, `Dawaii.Tests` (NUnit) |
| **Scale** | 167 source files, ~24,000 LOC |

### Outcome

| Category | Reported | Fixed | Not a bug | Deferred / needs manual test |
|---|---|---|---|---|
| Confirmed bugs | 15 (+1 found in fix phase) | **13** | **2** | 1 (BUG-011 authorization half — user's call) |
| Potential bugs | 7 | **4** | — | 3 (need a live MySQL / a printer / a click test) |
| UX issues | 5 | **4** | — | 1 (UX-001, out of scope for a safe change) |
| Architecture | 4 | 1 mitigated | — | 3 documented |

**Build:** Debug and Release both clean, 0 warnings introduced.
**Tests:** **315 / 315 pass** (303 before the fix phase; 12 added, 0 removed, 0 weakened).

### What is genuinely solid (unchanged)

- Password hashing — PBKDF2-SHA256, 100k iterations, constant-time compare.
- Sale and return persistence — one transaction with a `quantity_units >= n` oversell guard.
- FEFO allocation, ESC/POS raster engine, cross-backend SQL, schema parity (25 tables each).

---

# BUG-001 — Medicine taken by an employee is never removed from stock

**Original Severity:** CRITICAL
**Status:** FIXED

## Root Cause

`EmployeeService.AddMedicineExpense` read stock to validate availability, valued the medicine, and
wrote an `employee_expenses` row. It never decremented a batch — `_stock` was used at four call sites
in the file, all reads. Its own doc comment claimed the medicine "actually leaves the shelf".

## Fix Applied

- `IEmployeeRepository.AddMedicineExpense(expense, allocations)` — new transactional method.
- `SqliteEmployeeRepository.AddMedicineExpense` — inserts the expense row, decrements each batch
  under the same `WHERE is_disposed=0 AND quantity_units >= @u` guard the POS uses (a race with a
  sale on the last box rolls the whole expense back), and writes one `stock_adjustments` row per
  batch naming the employee, so the shrinkage is attributable exactly like a disposal.
- `EmployeeService.AddMedicineExpense` — allocates FEFO via `FefoAllocator.Allocate` and passes the
  plan down. The availability pre-check stays as a fast user-facing error.
- `FakeEmployeeRepository` — now decrements the fake shelf too, so the fake can no longer pass a
  bug the database would fail (which is exactly how this one survived 29 green tests).

## Files Changed

- `src/Dawaii.Core/Abstractions/IEmployeeRepository.cs`
- `src/Dawaii.Core/Data/SqliteEmployeeRepository.cs`
- `src/Dawaii.Core/Services/EmployeeService.cs`
- `tests/Dawaii.Tests/Fakes/FakeEmployeeRepository.cs`
- `tests/Dawaii.Tests/EmployeeServiceTests.cs` (+6 tests)

## Verification

- Build: PASS
- Tests: PASS — new tests `MedicineExpense_TakesTheUnitsOffTheShelf`,
  `_LowersWhatThePosCanThenSell`, `_CannotBeRepeatedForeverOnTheSameStock`,
  `_TakesFromTheBatchThatExpiresSoonest`, `_LeavesAnAdjustmentTrailNamingTheEmployee`,
  `_ThatIsRefused_TakesNothingOffTheShelf`. All six fail against the pre-fix code.
- Related workflow checked: money expenses (`AddMoneyExpense`) still use the plain `AddExpense` —
  cash does not touch stock. POS sale after an expense correctly sees reduced stock.
- Regression check: PASS — all 29 existing employee tests still green.

---

# BUG-002 — Deleting a drug that has ever been sold silently destroys its barcodes

**Original Severity:** HIGH
**Status:** NOT A BUG — behaviour is deliberate (V1.9). The **disclosure** was the real gap and is fixed.

## Reason

The scanner read the `else _codes.RemoveForItem(itemId)` branch as an accident. It is not. The
integration test `DeleteItem_ReleasesItsBarcode_EvenWhenTheRowIsKeptForSales_V19` asserts precisely
this, with the rationale in its body:

> *"A drug that has been sold is kept for its history, but it leaves the catalog — so its barcode is
> released too, or the replacement box could never be scanned in under it."*

A barcode is the manufacturer's EAN for the **product**, not for the database row. When a drug record
is retired and a replacement created, the same EAN must move to the new record. That is a real
pharmacy workflow and the code implements it on purpose.

The fix phase initially removed the branch on the strength of the audit; the test suite caught it
immediately. The change was reverted.

## What was actually wrong, and is fixed

The user was never **told**. `ItemsModule.ReportDeleteResult` said the items were "kept" and asked
about deactivation; answering *No* left an active, sellable, unscannable drug with no indication why.
The dialog now states that the barcode has been released and what *No* means.

## Files Changed

- `src/Dawaii.App/Modules/ItemsModule.cs` (dialog text only)
- `src/Dawaii.Core/Services/InventoryService.cs` (doc comment records the rule; code unchanged)

## Verification

- `DeleteItem_ReleasesItsBarcode_EvenWhenTheRowIsKeptForSales_V19`: PASS (was failing under the
  reverted attempt — the test did its job).

---

# BUG-003 — Restore cannot see its own backups (SQLite mode)

**Original Severity:** HIGH
**Status:** FIXED

## Root Cause

`SettingsModule.DoRestore` hard-coded `Filter = "SQL backup (*.sql)|*.sql"`. `BackupService.Ext`
is `".db"` in local mode. The dialog filtered to a format local mode never writes, with no "All
files" fallback.

## Fix Applied

- `BackupService.BackupExtension` — public, returns the backend's actual extension.
- `DoRestore` builds the filter from it and appends `كل الملفات (*.*)`, and opens on the configured
  backup folder.

## Files Changed

- `src/Dawaii.Core/Services/BackupService.cs`
- `src/Dawaii.App/Modules/SettingsModule.cs`

## Verification

- Build: PASS. Static: in local mode the filter is now `*.db`, matching `CreateBackup`'s output;
  in server mode `*.sql`. Manual click-through of the dialog recommended on the next deployment.

---

# BUG-004 — Cash paid to suppliers is missing from the expected drawer cash

**Original Severity:** HIGH
**Status:** FIXED

## Root Cause

`ShiftReportModule` computed `expectedCash = _cashOnly - moneyExpenses - _purchasesTotal`.
`supplier_payments` (V2.1) was read nowhere outside its own repository. There was also no way to
record *how* a supplier was paid, so simply subtracting every payment would have created the
mirror-image error for bank transfers.

## Fix Applied

- **Schema:** `supplier_payments.payment_method` (TEXT / VARCHAR(20)) added to both schemas, with an
  `EnsureColumn` migration. NULL on rows written before this build — read as **cash**, which is how
  distributors were in fact being paid.
- `SupplierPayment.PaymentMethod` + `IsCash`.
- `ISupplierRepository.PaymentsInRange` / `SupplierService.CashPaidToSuppliers(from, to)`.
- `SupplierService.RecordPayment` / `SettleInvoice` take an optional `paymentMethod` (default
  `"Cash"`, so every existing caller is unchanged).
- `InvoicePaymentForm` — a **نقداً من الدرج / تحويل بنكي** selector, and a confirmation before money
  moves (UX-004).
- `ShiftReportModule` — subtracts `CashPaidToSuppliers` and shows it as its own line
  (`سداد موردين (نقداً)`), so the figure is explainable.

## Files Changed

- `db/schema.sql`, `db/schema.mysql.sql`
- `src/Dawaii.Core/Data/DatabaseInitializer.cs`
- `src/Dawaii.Core/Models/SupplierPayment.cs`, `PurchaseInvoice.cs`
- `src/Dawaii.Core/Abstractions/ISupplierRepository.cs`
- `src/Dawaii.Core/Data/SqliteSupplierRepository.cs`
- `src/Dawaii.Core/Services/SupplierService.cs`
- `src/Dawaii.App/Forms/InvoicePaymentForm.cs`
- `src/Dawaii.App/Modules/ShiftReportModule.cs`
- `tests/Dawaii.Tests/SupplierInvoiceTests.cs` (+6 tests)

## Verification

- Build: PASS. Tests: PASS — `CashPaidToSuppliers_CountsMoneyHandedOverAtTheDoor`,
  `_CountsLaterPaymentsToo`, `_ExcludesBankTransfers`, `_IgnoresPaymentsOutsideThePeriod`,
  `SettlingAnInvoice_RecordsHowItWasPaid`, `APaymentRecordedBeforeV23_ReadsAsCash`.
- Regression check: PASS — all 49 pre-existing supplier tests green; door-payment on
  `RecordInvoice` records `"Cash"`.

---

# BUG-005 — "Remember me" stores the password and hands it to the next person

**Original Severity:** HIGH
**Status:** FIXED

## Root Cause

DPAPI `CurrentUser` scope protects against other *Windows* users. The threat on a pharmacy counter
is other *Dawaii* users sharing one Windows session.

## Fix Applied

- Only the **username** is stored. `Protect`/`Unprotect`, the entropy constant and the
  `System.Security.Cryptography` using are removed from `LoginForm`.
- A `last_user.txt` written by an older build (with the password on line 1) is rewritten without it
  on first load, so the stored secret is cleared rather than left on disk.
- The checkbox label now says **تذكر اسم المستخدم** — it no longer over-promises.
- The two empty `catch {}` blocks around the file I/O now log.

## Files Changed

- `src/Dawaii.App/Forms/LoginForm.cs`

## Verification

- Build: PASS. Static: no code path reads or writes a password to disk.
- Manual: tick the box, log in, relaunch — username filled, password box empty.

---

# BUG-006 — MySQL credentials in plaintext on every counter PC

**Original Severity:** HIGH
**Status:** FIXED (mitigated) — residual architectural risk documented

## Root Cause

`dawaii.ini` held `password=…` in plain text beside the exe.

## Fix Applied

- `AppConfig` reads `password_protected` (DPAPI, **`LocalMachine`** scope, app-specific entropy).
- A plaintext `password=` left by the installer is converted on first start: the ini is rewritten
  atomically (temp + replace) with `password_protected` and the plaintext line removed. If Program
  Files is read-only for that account the plaintext is kept and the app still starts — locking a
  pharmacy out of its database is worse than an ini that is still readable.
- A protected value that will not open (file copied from another PC) logs and falls back.

**Why machine scope, not user scope:** the counter login, the manager and any service account must
all open the same file. The protection is against reading the file in Notepad and against copying it
to another machine — the realistic paths — not against other accounts on the same PC.

## What this does NOT fix

The application's role model still runs inside the client, and every client still holds one
full-rights database credential. A determined user *of that machine* can still recover it. Closing
that requires either per-role database accounts or a server-side component — an architectural
change outside a safe fix. See ARCH-003.

## Files Changed

- `src/Dawaii.App/AppConfig.cs`

## Verification

- Build: PASS. Static review of the atomic-rewrite path. **Needs manual verification on a MySQL
  install:** start once, confirm `dawaii.ini` now holds `password_protected=` and no `password=`,
  and that the app still connects on the second start.

---

# BUG-007 — Returned stock is credited to disposed batches

**Original Severity:** MEDIUM
**Status:** FIXED

## Fix Applied

`SqliteSaleStore.RestoreUnits` now restores with `WHERE id=@b AND is_disposed=0` and checks the
affected row count. A return into a disposed batch is refused with a message telling the user to
record it as a stock adjustment instead — because a disposed batch means the stock was *destroyed*,
and quietly restocking a row the POS cannot sell from is worse than saying so.

## Files Changed

- `src/Dawaii.Core/Data/SqliteSaleStore.cs`

## Verification

- Build: PASS. Tests: PASS — all existing return tests green (none exercised the disposed case; the
  guard is the same pattern as the sale decrement, which is tested).

---

# BUG-008 — A customer's debt can be paid past zero into a negative balance

**Original Severity:** MEDIUM
**Status:** NOT A BUG — store credit is a documented design decision (D-06)

## Reason

Two tests encode this as intended:

- `Overpayment_ProducesNegativeBalance` — asserts `Balance == -50m` with the comment
  *"store credit (D-06)"*.
- `TotalOutstanding_SumsPositiveBalancesOnly` — asserts negatives are **excluded** from the total.

The second directly refutes the audit's claim that a negative balance "quietly offsets other
customers' real debt". It does not; `DebtLedger.TotalOutstanding` ignores it.

The supplier ledger refusing overpayment is a deliberate asymmetry: a supplier handed too much is a
matter to settle with that company; a customer leaving money on account is an everyday counter
transaction.

The fix phase initially added the guard; two tests failed; the change was reverted and the doc
comment on `RecordPayment` now records the decision so the next reader does not repeat the mistake.

## Verification

- `Overpayment_ProducesNegativeBalance`, `TotalOutstanding_SumsPositiveBalancesOnly`: PASS.

---

# BUG-009 — Unhandled UI exceptions are shown to the user and then discarded

**Original Severity:** MEDIUM
**Status:** FIXED

## Fix Applied

- `Application.ThreadException` logs the full exception via `Log.Error` before showing the message.
- `AppDomain.CurrentDomain.UnhandledException` is now subscribed (was absent).
- The startup backup `Task.Run` logs instead of `catch { }`.
- `LoginForm` remember-me I/O and `SettingsModule` restore log on failure.

## Files Changed

- `src/Dawaii.App/Program.cs`, `LoginForm.cs`, `SettingsModule.cs`, `PosModule.cs`

## Verification

- Build: PASS. `%LocalAppData%\Dawaii\logs\app.log` receives type, message and stack for any
  unhandled exception.

---

# BUG-010 — Role and account status are frozen at login

**Original Severity:** MEDIUM
**Status:** FIXED

## Fix Applied

- `Session.Refresh()` re-reads the signed-in user from the database. Returns `false` when the account
  is gone or deactivated; a database that cannot be reached returns `true` and keeps the session — a
  dropped network mid-shift must not throw the pharmacy out of the till.
- `MainForm.Navigate` calls it on **every screen change**. A deactivated account is signed out with a
  message; a changed role rebuilds the sidebar. Revocation now takes effect within one click on the
  affected counter instead of at the next voluntary logout.

## Files Changed

- `src/Dawaii.App/Session.cs`, `src/Dawaii.App/Forms/MainForm.cs`

## Verification

- Build: PASS. Manual: deactivate a signed-in user from another session; their next click on any
  sidebar item signs them out.

---

# BUG-011 — `DeleteAllItems` is not transactional and is open to the privileged employee

**Original Severity:** MEDIUM
**Status:** PARTIALLY FIXED — reporting fixed; authorization change **deferred to the owner**

## The reporting half — FIXED

A failure mid-sweep threw out of the loop before `deleted`/`withSales` were assigned, so
`ItemsModule` reported *"deleted 0"* when hundreds were already gone. The service now catches,
audits the partial progress (`INTERRUPTED after N deleted`), and throws a `DomainException` whose
Arabic message states how many were deleted and that a retry continues from there.

## The authorization half — DEFERRED, deliberately

The audit recommended `Guard.RequireAdmin`. The fix phase did **not** apply it, because:

1. The owner explicitly instructed three turns earlier that the privileged employee is to have full
   inventory access (*"make the employ with pravalge also have accses to invorty"*), and the
   commit `cf1dce4` records that decision.
2. The test `DeleteAllItems_AsCashier_Denied_ButAllowedForFullEmployee` asserts the current rule.

Narrowing a permission the owner just widened, on the fixer's own judgement, would be inventing a
business rule. **Recommendation to the owner:** catalog-wide deletion is a different act from
managing inventory, and it is one line (`RequireStaff` → `Guard.RequireAdmin`) plus one test flip
to make it manager-only. Your call.

## Files Changed

- `src/Dawaii.Core/Services/InventoryService.cs`

---

# BUG-012 — Customer and supplier searches silently return only the first 100 rows

**Original Severity:** MEDIUM
**Status:** FIXED

## Fix Applied

Both `DebtService.Search` and `SupplierService.Search` now cap at a public `SearchLimit = 500`,
which covers any realistic pharmacy. The limit is exposed so a screen can say when it is hit.

## Files Changed

- `src/Dawaii.Core/Services/DebtService.cs`, `SupplierService.cs`

---

# BUG-013 — Restoring a backup leaves the old session's user in place

**Original Severity:** MEDIUM
**Status:** FIXED

## Fix Applied

`DoRestore` now calls `Application.Restart()` after the success message. The restart is not advice
the user can decline — every write until then would carry a `user_id` from a database that no longer
exists.

## Files Changed

- `src/Dawaii.App/Modules/SettingsModule.cs`

---

# BUG-014 — Thermal receipts queued as "Restaurant POS Receipt"

**Original Severity:** LOW
**Status:** FIXED — `pDocName = "Dawaii Receipt"`.

**Files:** `src/Dawaii.App/Printing/RawPrinterHelper.cs`

---

# BUG-015 — `DebtService.CreateCustomer` performs no authorization check

**Original Severity:** LOW
**Status:** FIXED

## Fix Applied

Takes a `User actor`, calls `Guard.RequireUser` (any signed-in staff — a cashier opening a credit
account at the till stays the everyday case), and writes an audit row. `DebtService` gained an
optional `IAuditRepository` so the existing unit tests keep compiling; the app supplies it.

## Files Changed

- `src/Dawaii.Core/Services/DebtService.cs`, `src/Dawaii.App/AppServices.cs`,
  `src/Dawaii.App/Modules/CustomersModule.cs`, `tests/Dawaii.Tests/DebtTests.cs`

---

# BUG-016 — `EnsureColumn` created every migrated MySQL column as `INT NULL` *(found in fix phase)*

**Severity:** HIGH (network mode, upgraded databases only)
**Status:** FIXED

## Root Cause

```csharp
string type = _factory.Kind == DbKind.MySql ? "INT NULL" : sqliteType;
```

`EnsureColumn("sales", "payment_method", "TEXT")` therefore created an **integer** column on an
upgraded MySQL database. Writing `"Cash"` / `"Bankak"` / `"Fawry"` into it would fail or coerce to
0. Fresh installs never hit this (the schema script creates the column correctly); only a server
upgraded from before V1.3 did — the configuration least likely to be tested and most likely to be a
live pharmacy.

Found while adding the `supplier_payments.payment_method` migration for BUG-004, which needed a
TEXT column and would have received an INT.

## Fix Applied

`ColumnType(sqliteType)` maps `TEXT → TEXT NULL`, `REAL/NUMERIC → DOUBLE NULL`, else `INT NULL`.

## Files Changed

- `src/Dawaii.Core/Data/DatabaseInitializer.cs`

## Verification

- Build: PASS. **Needs manual verification** on a MySQL database created before V1.3 — check
  `sales.payment_method` type after upgrade (an existing wrongly-typed column is *not* retyped by
  this fix; that would need a one-off `ALTER TABLE … MODIFY`).

---

# Potential Bugs

## POT-001 — Duplicate transactions from a double-click — **FIXED**

`Theme.ActionButton` — every save, payment, delete and print in the app — now guards the handler
against re-entry and visibly disables the button for the duration. A click queued during a nested
message loop (print dialog, child form, GDI printing) is dropped instead of dispatched. Verified
statically that no handler relies on re-entry (no modeless `.Show()` from a button; no button that
opens itself). `IsDisposed` is checked before re-enabling, because handlers may close their form.

**Files:** `src/Dawaii.App/Ui/Theme.cs`

## POT-002 — Concurrent returns double-refund in MySQL — **NEEDS MANUAL VERIFICATION**

Not changed. The correct fix is `SELECT … FOR UPDATE` on the sale row at the head of `Refund`,
gated on `DbKind`, and it cannot be verified without a live MySQL and two clients. Static reasoning
stands: under REPEATABLE READ, two transactions both read `returned_total = 0`, both add units back,
last write wins. **Required test:** two clients, same invoice, return within the same second.

## POT-003 — Clicking a grid column header may throw — **FIXED**

`Theme.StyleGrid` sets `SortMode = NotSortable` on every column via `ColumnAdded`. This also makes
the row-index → `List<T>` mapping used by 14 screens safe **by construction** rather than by
accident (ARCH-001). Verified no screen sorts programmatically.

**Files:** `src/Dawaii.App/Ui/Theme.cs`

## POT-004 — Money stored as binary floating point — **NOT CHANGED**

Correct concern, wrong phase. Changing 30 columns' storage type is a data migration on live
pharmacies' databases, not a safe fix. `Db.GetMoney` rounds to 2dp on read, which contains it for
now. Recommended as its own change with its own migration and rollback plan.

## POT-005 — Integer overflow in cart quantity — **FIXED**

`SaleBuilder` multiplies in `long` and refuses anything over `int.MaxValue` with
*"الكمية كبيرة أكثر من اللازم"* instead of overflowing to a negative and telling the cashier the
quantity must be greater than zero.

**Files:** `src/Dawaii.Core/Services/SaleBuilder.cs`

## POT-006 — No login rate limiting — **NOT CHANGED**

Physical access required; PBKDF2 at 100k iterations is ~100ms/attempt. Low value against a
pharmacy's threat model; a lockout could also lock a cashier out mid-shift. Left as documented.

## POT-007 — Reports gated in the UI only — **FIXED**

`ReportService.BestSellers` and `DeadStock` now take a `User` and `Guard.RequireAdmin`. `Daily` /
`Range` already suppressed profit for non-admins and are unchanged. Callers in `ReportsModule` and
`ShiftReportModule` (both admin-only screens) updated.

**Files:** `src/Dawaii.Core/Services/ReportService.cs`, `ReportsModule.cs`, `ShiftReportModule.cs`

---

# UX Issues

## UX-001 — Everything runs on the UI thread — **NOT CHANGED**

Genuine, but introducing `async`/`Task.Run` across a synchronous WinForms codebase with a static
`Session` is not a small safe change — it is the kind of rewrite Phase 17 warns against. The
re-entrancy guard (POT-001) now at least makes a slow operation *look* busy rather than ignored.
Recommended as targeted follow-up on the three heaviest screens (range reports, exports, full stock).

## UX-002 — A failed thermal print falls back silently — **FIXED**

`PosModule.PrintReceipt` distinguishes "virtual printer, fall back quietly" (no reason) from "the
thermal head refused" (a reason): the latter is logged and the cashier is told to check the printer
before the GDI fallback is attempted. The outer catch now says the sale is saved and names the
reprint button — a printer that is off must not look like a failed sale.

**Files:** `src/Dawaii.App/Modules/PosModule.cs`

## UX-003 — The delete result dialog conceals what already happened — **FIXED**

See BUG-002. The dialog now says the barcode was released and what answering *No* means.

## UX-004 — No confirmation on supplier payments — **FIXED**

`InvoicePaymentForm` confirms amount, method and company before recording. See BUG-004.

## UX-005 — `Msg.Error(ex.Message)` is the universal error surface — **NOT CHANGED**

Forty call sites. A shared mapping from SQLite/Win32 exceptions to Arabic guidance is worthwhile but
is a cross-cutting UI change, not a bug fix. The logging added under BUG-009 means the raw message
is at least now recoverable.

---

# Architectural Weaknesses

| | Status |
|---|---|
| **ARCH-001** Grid selection by row index into a parallel list | **Mitigated** — grids are now non-sortable by construction (POT-003), so the coupling cannot break by accident. Still recommend binding model objects. |
| **ARCH-002** `Session` as mutable static global | Documented. `Session.Refresh()` (BUG-010) reduces one consequence. |
| **ARCH-003** Authorization enforced in the client process | Documented. BUG-006 closes the casual-read path; the structural exposure remains and is the single most important thing to plan for in network deployments. |
| **ARCH-004** Business rules duplicated across layers | Documented. The supplier repository's two price-mirroring paths already share `MirrorOntoItem`/`RepriceEveryBatch`; `InventoryService.SyncItemPricesFromLatestBatch` remains a third copy. |

---

# Fix & Refactoring Summary

## Bugs Fixed

- BUG-001 — medicine expenses now consume stock, transactionally, with an adjustment trail
- BUG-003 — restore dialog filters on the backend's real backup extension
- BUG-004 — cash paid to suppliers is subtracted from expected drawer cash (with a cash/bank choice)
- BUG-005 — "remember me" stores the username only; stored passwords are purged on first load
- BUG-006 — MySQL password stored DPAPI-protected; plaintext ini self-heals on first start
- BUG-007 — returns refuse to restock a disposed batch
- BUG-009 — unhandled exceptions logged (UI thread and AppDomain); empty catches replaced
- BUG-010 — session re-validated on every navigation; revocation takes effect within one click
- BUG-011 — interrupted catalog wipe now reports its true progress (authorization half deferred)
- BUG-012 — search cap raised to 500 and exposed
- BUG-013 — restore forces a restart
- BUG-014 — print job named "Dawaii Receipt"
- BUG-015 — `CreateCustomer` takes an actor and is audited
- BUG-016 — MySQL migrations create TEXT/DOUBLE columns correctly *(found in fix phase)*
- POT-001, POT-003, POT-005, POT-007
- UX-002, UX-003, UX-004

## Bugs Not Fixed

- **BUG-002** — NOT A BUG. Barcode release on a refused delete is intentional (V1.9, tested). Disclosure fixed instead.
- **BUG-008** — NOT A BUG. Negative balance = store credit (D-06, tested); negatives are already excluded from totals.
- **BUG-011 (authorization)** — DEFERRED. Contradicts the owner's explicit, recent instruction. One-line change; owner's decision.
- **POT-002** — NEEDS MANUAL VERIFICATION on live MySQL before changing.
- **POT-004** — correct concern, requires a data migration; separate change.
- **POT-006** — low value against the threat model.
- **UX-001, UX-005** — cross-cutting UI work, not safe bug fixes.

## Refactoring Performed

Deliberately minimal — every change is anchored to a finding above.

- `Theme.ActionButton` — single re-entrancy guard covering every transactional button.
- `Theme.StyleGrid` — single non-sortable rule covering every grid.
- `DatabaseInitializer.ColumnType` — one backend-type mapping replacing an inline hard-code.
- `SqliteEmployeeRepository` — gained `Exec`/`InsertScalar` transaction helpers matching the other
  repositories' pattern.
- No dead code removed (nothing was confirmed dead); no new abstractions, interfaces or dependencies.

## Files Changed

**Core** — `IEmployeeRepository.cs`, `ISupplierRepository.cs`, `DatabaseInitializer.cs`,
`SqliteEmployeeRepository.cs`, `SqliteSaleStore.cs`, `SqliteSupplierRepository.cs`,
`PurchaseInvoice.cs`, `SupplierPayment.cs`, `BackupService.cs`, `DebtService.cs`,
`EmployeeService.cs`, `InventoryService.cs`, `ReportService.cs`, `SaleBuilder.cs`,
`SupplierService.cs`

**App** — `AppConfig.cs`, `AppServices.cs`, `Program.cs`, `Session.cs`, `Ui/Theme.cs`,
`Forms/InvoicePaymentForm.cs`, `Forms/LoginForm.cs`, `Forms/MainForm.cs`,
`Modules/CustomersModule.cs`, `Modules/ItemsModule.cs`, `Modules/PosModule.cs`,
`Modules/ReportsModule.cs`, `Modules/SettingsModule.cs`, `Modules/ShiftReportModule.cs`,
`Printing/RawPrinterHelper.cs`

**Schema** — `db/schema.sql`, `db/schema.mysql.sql`

**Tests** — `EmployeeServiceTests.cs` (+6), `SupplierInvoiceTests.cs` (+6), `DebtTests.cs`,
`Fakes/FakeEmployeeRepository.cs`

## Database Changes

- **`supplier_payments.payment_method`** — `TEXT` (SQLite) / `VARCHAR(20)` (MySQL), nullable.
  Added by `EnsureColumn` on upgrade; NULL is read as cash. **Additive only** — no rows changed,
  no columns dropped, no data reset.
- `stock_adjustments` gains rows for medicine expenses going forward (reason `مصروف موظف (دواء)`).
  Historical expenses are **not** retro-applied to stock — the shelf has already been counted since,
  and re-deducting would double-count. The owner should expect a one-off stock count to reconcile
  past staff consumption.

## Security Changes

- Password no longer stored by "remember me"; existing stored passwords purged on first launch.
- MySQL credential DPAPI-protected (machine scope) with self-healing migration of the ini.
- Session re-validated on every navigation — deactivation/demotion is immediate.
- `ReportService` guarded at the service layer.
- `CreateCustomer` requires a signed-in actor and is audited.

## UI/UX Changes

- Every action button disables itself while working (duplicate-click protection).
- Grid headers no longer sortable (prevents both a crash and wrong-row actions).
- Supplier payment form: cash/bank selector + confirmation.
- Shift report: supplier cash shown as its own line.
- Delete-result dialog discloses barcode release.
- Restore: correct file filter, opens on the backup folder, restarts afterward.
- "تذكر اسم المستخدم" label.
- Printer failures produce specific, actionable Arabic messages.

## Printing Changes

- Job name corrected. Thermal-path failure surfaced to the cashier with the reason, logged, and the
  GDI fallback still attempted. No change to layout, encoding, RTL shaping or raster output.

## Verification

- **Build:** Debug PASS, Release PASS, 0 warnings introduced.
- **Tests:** 315 / 315 PASS (303 → 315; +12, −0). Two of the audit's findings were **caught by the
  existing suite** when the fix phase applied them — the reverts are recorded above.
- **Regression checks:** every changed public signature's callers updated and grepped
  (`CreateCustomer`, `BestSellers`, `DeadStock`, `RecordPayment`, `SettleInvoice`, `AddExpense`);
  no programmatic grid sorting; no modeless windows from action buttons.
- **Remaining risks:**
  - BUG-006 / BUG-016 need a live MySQL install to verify end-to-end.
  - POT-002 remains open in network mode.
  - Historical staff medicine consumption is not in the stock figures (see Database Changes).
  - The privileged employee can still wipe the catalog (BUG-011) — the owner's decision is awaited.
  - ARCH-003: the client-side authorization model is unchanged.

---

*Phase 2 complete. Application source modified only where a finding was confirmed against the code
and the tests; two findings rejected with evidence; one new defect found and fixed.*
