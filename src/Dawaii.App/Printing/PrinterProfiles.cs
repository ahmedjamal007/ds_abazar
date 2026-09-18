using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Dawaii.App.Printing
{
    /// <summary>Command profile resolved for a printer (port of printerProfiles.js).</summary>
    public sealed class PrinterProfile
    {
        public PrinterProfile(string key, string command, string label)
        {
            Key = key;
            Command = command;
            Label = label;
        }

        public string Key { get; }
        public string Command { get; }
        public string Label { get; }
    }

    public enum PrinterKind
    {
        Thermal,
        Virtual,
        Unknown,
    }

    public sealed class DetectedType
    {
        public DetectedType(PrinterKind kind, string profile, string label)
        {
            Kind = kind;
            Profile = profile;
            Label = label;
        }

        public PrinterKind Kind { get; }
        public string Profile { get; }
        public string Label { get; }
    }

    public static class PrinterProfiles
    {
        // Mirrors PROFILES in printerProfiles.js.
        public static readonly IReadOnlyDictionary<string, PrinterProfile> Profiles =
            new Dictionary<string, PrinterProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["auto"] = new PrinterProfile("auto", "epson", "تلقائي"),
                ["epson"] = new PrinterProfile("epson", "epson", "Epson ESC/POS"),
                ["escpos"] = new PrinterProfile("escpos", "escpos", "Generic ESC/POS"),
                ["generic"] = new PrinterProfile("generic", "generic", "Generic (Experimental)"),
                ["citizen"] = new PrinterProfile("citizen", "citizen", "Citizen"),
                ["star"] = new PrinterProfile("star", "starsbcs", "Star"),
                ["xprinter"] = new PrinterProfile("xprinter", "escpos", "XPrinter"),
                ["bixolon"] = new PrinterProfile("bixolon", "escpos", "Bixolon"),
                ["sunmi"] = new PrinterProfile("sunmi", "escpos", "Sunmi"),
            };

        private static readonly Regex VirtualRe = new Regex(
            @"pdf|xps|onenote|fax|document writer|microsoft print|print to file|save to",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EpsonRe = new Regex(@"epson|tm-t|tm t|tm-m|tm-u", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StarRe = new Regex(@"star|tsp|mc-print|mcp", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CitizenRe = new Regex(@"citizen|ct-s", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GenericThermalRe = new Regex(
            @"bixolon|xprinter|sunmi|pos-80|pos80|receipt|thermal|esc/pos",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IEnumerable<PrinterProfile> ProfileOptions => Profiles.Values;

        /// <summary>Returns the receiptline-style command key for a profile key.</summary>
        public static string ResolveCommand(string profileKey)
        {
            if (!string.IsNullOrEmpty(profileKey) && Profiles.TryGetValue(profileKey, out var profile))
            {
                return profile.Command;
            }
            return Profiles["epson"].Command;
        }

        /// <summary>Classifies a printer from its name/driver text (port of detectPrinterType).</summary>
        public static DetectedType DetectPrinterType(params string[] textParts)
        {
            var text = string.Join(" ", System.Array.FindAll(textParts ?? new string[0], p => !string.IsNullOrEmpty(p))).ToLowerInvariant();

            if (VirtualRe.IsMatch(text))
            {
                return new DetectedType(PrinterKind.Virtual, "auto", "افتراضي / PDF");
            }
            if (EpsonRe.IsMatch(text))
            {
                return new DetectedType(PrinterKind.Thermal, "epson", "Epson ESC/POS");
            }
            if (StarRe.IsMatch(text))
            {
                return new DetectedType(PrinterKind.Thermal, "star", "Star");
            }
            if (CitizenRe.IsMatch(text))
            {
                return new DetectedType(PrinterKind.Thermal, "citizen", "Citizen");
            }
            if (GenericThermalRe.IsMatch(text))
            {
                return new DetectedType(PrinterKind.Thermal, "escpos", "Generic ESC/POS");
            }
            return new DetectedType(PrinterKind.Unknown, "escpos", "غير معروف");
        }

        public static bool IsVirtualPrinterName(string text)
        {
            return !string.IsNullOrEmpty(text) && VirtualRe.IsMatch(text);
        }
    }
}
