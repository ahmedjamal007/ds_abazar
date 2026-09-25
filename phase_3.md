# Phase 3 — the whole solution builds and passes on .NET 10

**Done.** 559 tests pass on **both** `net48` and `net10.0-windows`. The app still ships as `net48`.

```
net48            559 passed   2m 04s
net10.0-windows  559 passed   0m 41s
```

Phase 4 (PdfSharp) turned out not to exist in the form it was planned. See below.

## What changed

`Dawaii.App` and `Dawaii.Tests` both multi-target now, matching Core:

```xml
<TargetFrameworks>net48;net10.0-windows</TargetFrameworks>
```

- **WPF** is `<Reference Include="WindowsBase" />` and friends on `net48`, where it comes from the
  GAC by assembly name, and `<UseWPF>true</UseWPF>` on modern .NET, where those names do not resolve
  at all. Both conditioned on the target.
- **DPAPI** (`System.Security`) is a GAC reference on `net48` and part of the shared framework on
  modern .NET — referencing the package there earns NU1510, so it is named only for `net48`.
- **WFO1000** is suppressed, with reasons. See below.

## WFO1000, and why it is suppressed rather than fixed

The first `net10.0-windows` build produced two dozen errors, all of them this analyzer, which asks
every public property on a custom control to declare how the **Windows Forms Designer** should
serialize it. On modern .NET it is an error by default.

This solution contains no designer surface whatsoever: not one `.Designer.cs`, not one `.resx`. Every
screen is built by hand in code and these controls are never dropped onto a canvas. The analyzer is
describing a failure mode that cannot occur here, and the alternative was
`DesignerSerializationVisibility` attributes on two dozen properties to satisfy a tool nobody runs.

It is suppressed at project level with that reasoning written next to it, and should be revisited the
day the project gains a designer file.

## The one real failure, and what it actually was

`Pdf_MultiSection_WritesValidPdf` was the single test to fail on .NET 10:

```
System.NotSupportedException : No data is available for encoding 1252.
  at PdfSharp.Pdf.Internal.PdfEncoders.get_WinAnsiEncoding()
  at PdfSharp.Pdf.PdfDocument.Save(String path)
```

**This was not a PdfSharp incompatibility.** It is the identical root cause as the Arabic receipt bug
fixed two commits ago, surfacing somewhere entirely unrelated: modern .NET does not carry the legacy
Windows code pages, and `Encoding.GetEncoding` throws until a provider is registered. The receipt
printer wanted CP1256; PdfSharp wants CP1252 for WinAnsi strings, and reaches for it while saving
*any* document — even one made only of images, because the metadata dates go through it.

The fix is a single registration point, `Dawaii.Core.Printing.LegacyEncodings`, called from
`Program.Main` at startup and defensively from the two components that depend on it, since a test run
or any other host that never calls `Main` still has to work.

**PdfSharp 1.50.5147 then worked unchanged on .NET 10** — the 2016 .NET Framework build, running
against the .NET 10 runtime, producing valid PDFs. The step budgeted as the one most likely to fight
back never fought.

## What is still outstanding on PdfSharp

It builds and runs, but it is still resolved through the compatibility shim:

```
warning NU1701: Package 'PDFsharp 1.50.5147' was restored using '.NETFramework,Version=v4.8'
instead of the project target framework 'net10.0-windows7.0'.
```

That warning is correct and should not be waved away: a `net48` assembly is being loaded on .NET 10
and only the code paths the tests exercise have been proven. Upgrading to PDFsharp 6.x remains worth
doing — but it is now an ordinary dependency upgrade to be done deliberately, not a migration
blocker, and the export tests are the net for it.

## What is left

4. **PdfSharp 6.x**, on its own — downgraded from blocker to housekeeping.
5. **Flip the default.** Both targets pass; at some point `net10.0-windows` becomes what the installer
   ships and `net48` becomes the fallback, or is dropped. That changes what gets installed on a
   pharmacy PC — .NET 10 is not preinstalled on Windows the way .NET Framework is — so the installer
   needs to carry or fetch the runtime, and that is its own piece of work.
6. **Nullable reference types**, with the `CA1305` analyzers. Highest value left, several hundred
   warnings, its own phase.
