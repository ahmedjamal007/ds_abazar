// Records and `init` accessors compile down to a modreq on a type called IsExternalInit. The C#
// compiler emits the reference; .NET Framework's class library does not contain the type, so on
// net48 every `init` setter and every positional record fails to build with CS0518 — a language
// feature withheld by a missing marker class rather than by anything the runtime cannot do.
//
// Declaring it ourselves is the sanctioned fix: the compiler only needs the name to exist. It is
// compiled ONLY for .NET Framework. On .NET 10 the real one is in the BCL, and shipping a second
// copy would make every `init` in the codebase ambiguous — so the whole file disappears there, and
// this shim quietly stops existing the moment it stops being needed.

#if NETFRAMEWORK

namespace System.Runtime.CompilerServices
{
    /// <summary>Marker the compiler requires for `init` accessors and records. Never referenced by hand.</summary>
    internal static class IsExternalInit
    {
    }
}

#endif
