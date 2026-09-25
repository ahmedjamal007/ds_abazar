# Phase 1 — modern C# on the existing framework

**Done.** 553 tests pass. Nothing retargeted; the app still builds and ships as `net48`.

The point of this phase is to separate two things that usually get done together and then blamed on
each other: *which language the code is written in* and *which runtime it executes on*. Phase 1 moves
only the first. If something breaks here, it is the compiler, and there is no runtime change in the
picture to argue about.

## What changed

`LangVersion` went from `7.3` to `latest` in all three projects:

| Project | Target | LangVersion |
|---|---|---|
| `Dawaii.Core` | `net48` | `latest` |
| `Dawaii.App` | `net48` | `latest` |
| `Dawaii.Tests` | `net48` | `latest` |

Plus one new file, `src/Dawaii.Core/Compatibility/IsExternalInit.cs`.

## What that actually buys, verified by compiling it

C# 7.3 was six years behind. The compiler that ships with the .NET 8 SDK understands C# 12, and it
will happily emit `net48` assemblies from it. I probed the boundary rather than assuming it — the
following all compile against `net48` today:

- **C# 8** — switch expressions, `using` declarations, `??=`, nullable reference types
- **C# 9** — target-typed `new()`, records, `init` accessors
- **C# 10/11** — file-scoped namespaces, raw string literals (`"""..."""`), required members
- **C# 12** — collection expressions (`[1, 2, 3]`), primary constructors

The one thing that did *not* work out of the box was records and `init`, and the reason is worth
knowing because it looks like a runtime limitation and is not one. Both compile down to a modreq on
`System.Runtime.CompilerServices.IsExternalInit`. The compiler emits the reference; .NET Framework's
class library simply does not contain the type, so every `init` setter failed with CS0518. Nothing
about the runtime is incapable — a marker class is missing.

Declaring it ourselves is the sanctioned fix, and the shim is wrapped in `#if NETFRAMEWORK`. On
.NET 10 the real type is in the BCL, and a second copy would make every `init` in the codebase
ambiguous — so the file compiles to nothing there and stops existing the moment it stops being
needed.

## What was deliberately not done

**No existing code was rewritten.** Nothing was converted to records, no namespaces were made
file-scoped, no `switch` was turned into an expression. A phase that flips a compiler switch and then
touches 200 files cannot tell you which of the two broke something. The new syntax is *available*;
adopting it is ordinary work to be done where it makes a file better, not a migration.

**Nullable reference types are not on yet.** They are the highest-value item in the whole upgrade —
turned on with the `CA1305`/`CA1307` analyzers they would have caught the culture bugs fixed in the
previous commit at compile time. But `<Nullable>enable</Nullable>` across this codebase means
hundreds of warnings to work through, and that is its own phase with its own review, not a footnote
to a `LangVersion` bump.

## Prerequisite for Phase 2

Phase 2 retargets `Dawaii.Core` to `net10.0`, and **it cannot start until the .NET 10 SDK is
installed on the build machine.** As of this commit:

```
$ dotnet --list-sdks
8.0.422 [C:\Program Files\dotnet\sdk]
```

It installs alongside .NET 8 without disturbing it: <https://dotnet.microsoft.com/download/dotnet/10.0>

## The phases after this one

2. **`Dawaii.Core` → `net10.0`, alone.** It has no Windows dependency and no blocking packages, so it
   should go green by itself — which is what proves the Core/App split is real rather than nominal.
3. **`Dawaii.Tests` → `net10.0-windows`**, and run everything. This is the first run where the
   culture tests from the previous commit are doing their job: they are the only reason a green suite
   after retargeting will mean anything, since every test before them ran as `en-US`.
4. **`Dawaii.App` → `net10.0-windows`**, with `<UseWPF>true</UseWPF>` — `Printing/ReceiptDocument.cs`
   uses `FormattedText` and `DrawingContext`, which resolve from the GAC on `net48` and will not
   otherwise resolve at all.
5. **PdfSharp, on its own and last.** 1.50.5147 is a 2016 .NET Framework build and 6.x changed its
   API. It is the step most likely to fight back, and it should not be able to contaminate the
   diagnosis of anything else when it does.

See `docs/dotnet10-upgrade.md` for the full assessment behind this ordering.
