using System.Text;
using gsm.Models;
using gsm.Services;

namespace gsm.Tests;

public sealed class ExcelClipboardFormatterTests
{
    [Fact]
    public void Build_LongCcidIsLiteralTextWithoutFormulaWrapper()
    {
        var columns = new[] { new ComTableColumnDefinition("Serial", "CCID") };
        IReadOnlyList<string>[] rows = [["89840200011639727963"]];

        ExcelClipboardPayload payload = ExcelClipboardFormatter.Build(columns, rows);

        Assert.Equal($"CCID{Environment.NewLine}89840200011639727963{Environment.NewLine}", payload.UnicodeText);
        Assert.Contains("<th nowrap=\"nowrap\" style=", payload.Html);
        Assert.Contains("<td nowrap=\"nowrap\" style=", payload.Html);
        Assert.Contains("white-space:nowrap", payload.Html);
        Assert.Contains("mso-wrap-style:none", payload.Html);
        Assert.Contains("mso-number-format:'\\@'", payload.Html);
        Assert.Contains(">89840200011639727963</td>", payload.Html);
        Assert.DoesNotContain("=&quot;89840200011639727963", payload.Html);
        Assert.DoesNotContain("=\"89840200011639727963\"", payload.UnicodeText);
    }

    [Fact]
    public void Build_UsesCorrectUtf8ByteOffsetsForVietnameseContent()
    {
        var columns = new[] { new ComTableColumnDefinition("Status", "Trạng thái") };
        IReadOnlyList<string>[] rows = [["Đã nhận SIM ✅"]];

        string html = ExcelClipboardFormatter.Build(columns, rows).Html;
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        int startHtml = ReadOffset(html, "StartHTML:");
        int endHtml = ReadOffset(html, "EndHTML:");
        int startFragment = ReadOffset(html, "StartFragment:");
        int endFragment = ReadOffset(html, "EndFragment:");

        Assert.StartsWith("<html", Encoding.UTF8.GetString(bytes[startHtml..endHtml]));
        string fragment = Encoding.UTF8.GetString(bytes[startFragment..endFragment]);
        Assert.StartsWith("<table ", fragment);
        Assert.EndsWith("</table>", fragment);
        Assert.Contains("Đã nhận SIM ✅", fragment);
        Assert.Equal(bytes.Length, endHtml);
    }

    [Fact]
    public void Build_EncodesHtmlAndSanitizesTsvControlCharacters()
    {
        var columns = new[] { new ComTableColumnDefinition("LastMessageContent", "Nội dung") };
        IReadOnlyList<string>[] rows = [["<b>A&B</b>\tMột\r\nHai"]];

        ExcelClipboardPayload payload = ExcelClipboardFormatter.Build(columns, rows);

        Assert.Contains("&lt;b&gt;A&amp;B&lt;/b&gt; Một Hai", payload.Html);
        Assert.DoesNotContain("<b>A&B</b>", payload.Html);
        Assert.Equal(
            $"Nội dung{Environment.NewLine}<b>A&B</b> Một Hai{Environment.NewLine}",
            payload.UnicodeText);
    }

    private static int ReadOffset(string html, string field)
    {
        int start = html.IndexOf(field, StringComparison.Ordinal) + field.Length;
        return int.Parse(html.AsSpan(start, 10));
    }
}
