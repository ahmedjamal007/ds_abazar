using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace Dawaii.App.Ui
{
    /// <summary>Loads the embedded logo (and derives the window icon from it) with no external files.</summary>
    public static class AppImages
    {
        private static Image _logo;
        private static Icon _icon;

        public static Image Logo => _logo ?? (_logo = LoadEmbedded("Resources.logo.png"));

        public static Icon AppIcon
        {
            get
            {
                if (_icon != null) return _icon;
                try
                {
                    // The .exe embeds dawaii.ico as its Win32 icon; reuse it for every window.
                    _icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
                }
                catch
                {
                    _icon = null;
                }
                return _icon;
            }
        }

        private static Image LoadEmbedded(string relativeName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            // Resolve by suffix: the manifest prefix is the RootNamespace (e.g. "Dawaii.App"),
            // which is not necessarily the assembly name.
            string fullName = null;
            foreach (string n in asm.GetManifestResourceNames())
                if (n.EndsWith("." + relativeName, System.StringComparison.OrdinalIgnoreCase))
                { fullName = n; break; }
            if (fullName == null) return null;
            using (Stream s = asm.GetManifestResourceStream(fullName))
            {
                if (s == null) return null;
                // Copy to a memory stream so the image is not tied to the resource stream lifetime.
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    ms.Position = 0;
                    return Image.FromStream(ms);
                }
            }
        }
    }
}
