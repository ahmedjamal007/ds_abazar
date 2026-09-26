using System.Globalization;
using System.Text;

namespace Erp.TelegramBot.Reports;

/// <summary>A file to send, built entirely in memory.</summary>
/// <param name="Name">The file name the manager sees and saves.</param>
/// <param name="Bytes">The whole file. No temp file is ever written to disk.</param>
public sealed record OutgoingFile(string Name, byte[] Bytes);

/// <summary>
/// Builds a CSV in memory.
///
/// CSV rather than xlsx or PDF, for three reasons that all point the same way: it opens on a phone,
/// it opens in Excel, and it needs no library — so the bot has no dependency that could break the
/// one thing a manager might actually forward to an accountant.
///
/// Two details it would be easy to get wrong and never notice:
///
/// The **BOM**. Without it Excel opens a UTF-8 file as the system code page and every Arabic drug
/// name becomes mojibake. The file is perfectly valid; it just looks like corruption to the person
/// who asked for it. The pharmacy's own exporter learned this already.
///
/// The **leading apostrophe on anything Excel would reinterpret**. A value starting with =, +, - or
/// @ is treated as a formula, which is both wrong (a drug name is not a formula) and a genuine
/// injection route into whoever opens the file. Prefixed so it stays text.
/// </summary>
public static class ReportFile
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>
    /// A CSV with a title line, then one or more sections each with its own header row.
    /// </summary>
    public static OutgoingFile Csv(string fileName, string title, IEnumerable<CsvSection> sections)
    {
        var sb = new StringBuilder();
        sb.Append(Cell(title)).Append("\r\n\r\n");

        foreach (CsvSection section in sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Heading))
                sb.Append(Cell(section.Heading)).Append("\r\n");

            if (section.Columns.Count > 0)
                sb.Append(string.Join(",", section.Columns.Select(Cell))).Append("\r\n");

            foreach (string[] row in section.Rows)
                sb.Append(string.Join(",", row.Select(Cell))).Append("\r\n");

            sb.Append("\r\n");
        }

        // UTF-8 WITH the byte-order mark. See the class note: without it every Arabic name in this
        // file looks like corruption the moment Excel opens it.
        var bytes = new List<byte>(Encoding.UTF8.GetPreamble());
        bytes.AddRange(Encoding.UTF8.GetBytes(sb.ToString()));

        return new OutgoingFile(fileName, bytes.ToArray());
    }

    /// <summary>Money, two decimals, Latin digits — the form every other export in this program uses.</summary>
    public static string Money(decimal value) => value.ToString("0.00", En);

    public static string Int(int value) => value.ToString(En);

    public static string Date(DateTime value) => value.ToString("yyyy-MM-dd", En);

    public static string DateTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", En);

    /// <summary>
    /// One CSV cell: quoted when it has to be, and never allowed to become a formula.
    /// </summary>
    private static string Cell(string? value)
    {
        string v = value ?? "";

        // Excel and LibreOffice both execute a cell beginning with these. A drug name is not a
        // formula, and a file forwarded to an accountant should not be able to run one.
        if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@'))
            v = "'" + v;

        bool needsQuotes = v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r');
        if (!needsQuotes) return v;

        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>One titled block of rows inside a CSV.</summary>
public sealed class CsvSection
{
    public string? Heading { get; set; }
    public IReadOnlyList<string> Columns { get; set; } = [];
    public List<string[]> Rows { get; } = [];

    public CsvSection Add(params string[] row)
    {
        Rows.Add(row);
        return this;
    }
}
