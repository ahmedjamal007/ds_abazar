using System;
using System.Text;

namespace Dawaii.Core.Printing
{
    /// <summary>
    /// Makes the old single-byte code pages available (V2.5).
    ///
    /// .NET Framework's runtime carries every Windows code page. Modern .NET carries only Unicode and
    /// ASCII; everything else moved into System.Text.Encoding.CodePages and
    /// <see cref="Encoding.GetEncoding(int)"/> throws NotSupportedException until a provider is
    /// registered. Two things in this program depend on that and neither is obvious:
    ///
    ///   - CP1256, the Arabic code page. The thermal printer is sent ESC t 22 and then the text; if
    ///     the encoding is missing the bytes go out as something else and a roll of garbage comes out,
    ///     with no exception thrown anywhere.
    ///   - CP1252, which PdfSharp uses internally for WinAnsi strings while saving ANY document. That
    ///     one does throw, from deep inside the library, when a report is exported.
    ///
    /// Registering is process-wide, idempotent and cheap, so it is done once at startup and again
    /// defensively from the two places that care — a test run or any other host that never calls
    /// Program.Main still has to work.
    /// </summary>
    public static class LegacyEncodings
    {
        private static readonly object Gate = new object();
        private static bool _done;

        /// <summary>
        /// Registers the legacy code-page provider if this runtime needs one. Safe to call repeatedly
        /// and from anywhere; does nothing on .NET Framework, which already has the code pages.
        /// </summary>
        public static void Register()
        {
            if (_done) return;
            lock (Gate)
            {
                if (_done) return;
                _done = true;

                try
                {
                    // Reflection rather than a direct reference so the same source still compiles for
                    // .NET Framework, where the type does not exist and none of this is needed.
                    Type provider = Type.GetType(
                        "System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                    if (provider == null) return;

                    var instance = provider.GetProperty("Instance")?.GetValue(null) as EncodingProvider;
                    if (instance != null) Encoding.RegisterProvider(instance);
                }
                catch
                {
                    // A runtime with nothing to register. The callers each fall back on their own terms.
                }
            }
        }

        /// <summary>
        /// True when <paramref name="codePage"/> can actually be used. Lets a caller find out rather
        /// than discover it from a garbled receipt or an exception halfway through saving a report.
        /// </summary>
        public static bool IsAvailable(int codePage)
        {
            Register();
            try
            {
                return Encoding.GetEncoding(codePage) != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
