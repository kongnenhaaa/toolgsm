using System.Globalization;
using System.Text;
using gsm.Models;

namespace gsm.Services;

public sealed record ExcelClipboardPayload(string UnicodeText, string Html);

/// <summary>
/// Builds a dual-format clipboard payload. Excel reads the HTML table and
/// therefore keeps identifiers such as a 20-digit CCID as literal text, while
/// other applications can still use the plain tab-separated representation.
/// </summary>
public static class ExcelClipboardFormatter
{
    private const string ClipboardHeaderTemplate =
        "Version:1.0\r\n" +
        "StartHTML:{0:D10}\r\n" +
        "EndHTML:{1:D10}\r\n" +
        "StartFragment:{2:D10}\r\n" +
        "EndFragment:{3:D10}\r\n";

    private const string HtmlPrefix =
        "<html xmlns:x=\"urn:schemas-microsoft-com:office:excel\">" +
        "<head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";

    private const string HtmlSuffix = "<!--EndFragment--></body></html>";

    public static ExcelClipboardPayload Build(
        IReadOnlyList<ComTableColumnDefinition> columns,
        IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var normalizedHeaders = columns
            .Select(column => NormalizeCell(column.Header))
            .ToArray();
        var normalizedRows = new List<string[]>();

        foreach (IReadOnlyList<string> row in rows)
        {
            if (row.Count != columns.Count)
            {
                throw new ArgumentException(
                    $"Expected {columns.Count} cells but received {row.Count}.",
                    nameof(rows));
            }

            normalizedRows.Add(row.Select(NormalizeCell).ToArray());
        }

        return new ExcelClipboardPayload(
            BuildUnicodeText(normalizedHeaders, normalizedRows),
            BuildHtml(columns, normalizedHeaders, normalizedRows));
    }

    private static string BuildUnicodeText(
        IReadOnlyList<string> headers,
        IReadOnlyList<string[]> rows)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join('\t', headers));
        foreach (string[] row in rows)
            text.AppendLine(string.Join('\t', row));
        return text.ToString();
    }

    private static string BuildHtml(
        IReadOnlyList<ComTableColumnDefinition> columns,
        IReadOnlyList<string> headers,
        IReadOnlyList<string[]> rows)
    {
        var fragment = new StringBuilder(
            "<table style=\"white-space:nowrap;mso-wrap-style:none;\"><thead><tr>");
        foreach (string header in headers)
        {
            fragment.Append("<th nowrap=\"nowrap\" style=\"white-space:nowrap;vertical-align:bottom;mso-wrap-style:none;\">")
                .Append(HtmlEncode(header))
                .Append("</th>");
        }

        fragment.Append("</tr></thead><tbody>");
        foreach (string[] row in rows)
        {
            fragment.Append("<tr>");
            for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                fragment.Append("<td nowrap=\"nowrap\" style=\"white-space:nowrap;vertical-align:bottom;mso-wrap-style:none;");
                if (ShouldKeepAsText(columns[columnIndex].Name))
                    fragment.Append("mso-number-format:'\\@';");
                fragment.Append("\">")
                    .Append(HtmlEncode(row[columnIndex]))
                    .Append("</td>");
            }
            fragment.Append("</tr>");
        }
        fragment.Append("</tbody></table>");

        return WrapClipboardHtml(fragment.ToString());
    }

    private static bool ShouldKeepAsText(string columnName) =>
        !columnName.Equals("Stt", StringComparison.OrdinalIgnoreCase)
        && !columnName.Equals("Balance", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCell(string? value) =>
        (value ?? string.Empty)
            .Replace("\t", " ")
            .Replace("\r\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ");

    private static string HtmlEncode(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string WrapClipboardHtml(string fragment)
    {
        string placeholderHeader = string.Format(
            CultureInfo.InvariantCulture,
            ClipboardHeaderTemplate,
            0,
            0,
            0,
            0);

        int startHtml = Encoding.UTF8.GetByteCount(placeholderHeader);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(HtmlPrefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(HtmlSuffix);

        string header = string.Format(
            CultureInfo.InvariantCulture,
            ClipboardHeaderTemplate,
            startHtml,
            endHtml,
            startFragment,
            endFragment);

        return header + HtmlPrefix + fragment + HtmlSuffix;
    }
}
