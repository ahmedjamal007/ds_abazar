# Phase 2 — `Dawaii.Core` runs on .NET 10

**Done.** 556 tests pass on `net48`, and `Dawaii.Core` builds **warning-free** for both `net48` and
`net10.0`. The app still ships as `net48`.

## Multi-targeted, not moved

```xml
<TargetFrameworks>net48;net10.0</TargetFrameworks>
```

A `net48` project cannot reference a `net10.0` one, so retargeting Core outright would have broken
`Dawaii.App` and `Dawaii.Tests` on the same commit and left three things to debug at once. Building
both instead means the existing app keeps consuming the `net48` asset while the `net10.0` asset
proves the port compiles — and the two stay in step until the app is ready to follow.

## Core compiled on .NET 10 unchanged

No source edits were needed to make it build. That is the Core/App split from `2809c36` paying off in
the only way that counts: the domain layer had no Windows dependency to unpick.

Two things did need dealing with.

### A critical advisory, and only on .NET 10

```
warning NU1904: Package 'System.Drawing.Common' 4.7.0 has a known critical severity
vulnerability — GHSA-rxg9-xrhp-64gj
```

`QRCoder` 1.4.3 pulls `System.Drawing.Common` in transitively. On `net48` it never showed, because
`System.Drawing` comes from the GAC there and no package is involved. The retarget is what exposed
it. QRCoder is now 1.8.0, which resolves 6.0.0 instead and is clean.

Worth noting the code never used it: `QrCodeGenerator` calls `PngByteQRCode`, which is pure managed.
The vulnerable assembly came along for the ride and would have shipped anyway.

### Password hashing, and the test that had to exist first

`Rfc2898DeriveBytes`' constructors are obsolete on modern .NET (SYSLIB0060); the sanctioned route is
the static `Rfc2898DeriveBytes.Pbkdf2`, which does not exist on `net48`. So `Derive` is now
conditioned on the target.

**The danger here is not the compile.** Both branches must produce byte-for-byte identical output,
because a stored hash was derived by whichever branch was compiled at the time, and every password in
every pharmacy already running this depends on the other one agreeing. Get it wrong and nobody can
log in after the upgrade — with no error, no crash, and no clue.

Every existing password test hashed and then verified with the same code, so **all of them would have
passed even if the derivation changed** — the round trip would simply have agreed with itself. That
gap was closed before the API was touched:

- A hash computed by **Python's `hashlib.pbkdf2_hmac`** — an implementation that knows nothing about
  this codebase — is pinned in `PasswordHasherTests`, and `Verify` must still accept it.
- That test passed against the old code first, proving this codebase matches the standard.
- Then the API changed, and it was run **on the .NET 10 runtime**: a hash stored by the `net48` build
  still verifies, and the wrong password is still refused.

## The culture net, checked on the real runtime

The previous commit built tests for what ICU would do. Running Core's actual code on .NET 10.0.12
confirmed it rather than predicting it:

| | result |
|---|---|
| ICU active | yes |
| `ar-SD` decimal separator | **U+066B** (٫), group **U+066C** (٬) |
| bare `decimal.TryParse("1250.50")` under `ar-SD` | **`False`** |
| `MoneyInput` across 7 cultures | all correct, never 125050 |
| Arabic-Indic digits (`١٢٥٠٫٥٠`) | correct |
| receipts encode as CP1256 | yes, provider registration works |

That third row is the bug, observed and not merely argued: it is exactly what `StatementForm` used to
do when reading a customer's debt repayment, and on .NET 10 it would simply have started refusing
valid amounts.

## What is left

3. **`Dawaii.App` → multi-target `net10.0-windows`.** Needs `<UseWPF>true</UseWPF>` —
   `Printing/ReceiptDocument.cs` uses `FormattedText` and `DrawingContext`, which resolve from the
   GAC on `net48` and will not otherwise resolve at all. `Dawaii.Tests` follows it, since it
   references the app and cannot move first.
4. **PdfSharp, alone and last.** 1.50.5147 is a 2016 .NET Framework build; 6.x changed its API. The
   step most likely to fight back, and it should not be able to contaminate anything else when it
   does.
5. **Nullable reference types**, with the `CA1305` analyzers. Highest value left, several hundred
   warnings, its own phase.
