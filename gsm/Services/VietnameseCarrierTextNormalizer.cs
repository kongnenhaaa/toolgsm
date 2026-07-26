using System.Text.RegularExpressions;

namespace gsm.Services;

/// <summary>
/// Restores Vietnamese diacritics only for known carrier templates that arrive
/// as intentional GSM-7/ASCII text. This is display normalization, not decoder
/// recovery: unknown text, OTPs, URLs and service codes are left unchanged.
/// </summary>
public static partial class VietnameseCarrierTextNormalizer
{
    public static string RestoreForDisplay(string? content)
    {
        string text = content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)
            || ContainsVietnameseDiacritics(text))
        {
            return text;
        }

        if (RemainingDataSignature().IsMatch(text))
            return RestoreRemainingDataTemplate(text);

        if (NoPackageSignature().IsMatch(text))
            return RestoreNoPackageTemplate(text);

        return text;
    }

    private static string RestoreRemainingDataTemplate(string text)
    {
        text = Replace(
            text,
            @"\bDung\s+luong\s+Data\s+con\s+lai\s+cua\s+goi\b",
            "Dung lượng Data còn lại của gói");
        text = Replace(text, @"\bHSD\s+goi\s+cuoc\b", "HSD gói cước");
        text = Replace(
            text,
            @"\bQK\s+co\s+the\s+tra\s+cuu\s+chi\s+tiet\s+dung\s+luong\s+con\s+lai\s+va\s+cac\b",
            "QK có thể tra cứu chi tiết dung lượng còn lại và các");
        text = Replace(text, @"\bngay(?=\s+\d{1,2}/\d{1,2}/\d{4}\b)", "ngày");
        text = Replace(text, @"\buu\s+dai\b", "ưu đãi");
        text = Replace(text, @"\btruy\s+cap\b", "truy cập");
        text = Replace(text, @"\bung\s+dung\b", "ứng dụng");
        return text;
    }

    private static string RestoreNoPackageTemplate(string text)
    {
        text = Replace(
            text,
            @"\bQuy\s+khach\s+hien\s+khong\s+dang\s+ky\s+su\s+dung\s+goi\s+cuoc\b",
            "Quý khách hiện không đăng ký sử dụng gói cước");
        text = Replace(text, @"\bVui\s+long\s+soan\s+tin\b", "Vui lòng soạn tin");
        text = Replace(
            text,
            @"\bgui\s+900\s+hoac\s+truy\s+cap\s+My\s+VNPT\s+tai\b",
            "gửi 900 hoặc truy cập My VNPT tại");
        text = Replace(
            text,
            @"\bde\s+tham\s+khao\s+cac\s+goi\s+cuoc\b",
            "để tham khảo các gói cước");
        text = Replace(text, @"\buu\s+dai\b", "ưu đãi");
        return text;
    }

    private static bool ContainsVietnameseDiacritics(string text) =>
        VietnameseDiacritic().IsMatch(text);

    private static string Replace(string text, string pattern, string replacement) =>
        Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(
        @"\bDung\s+luong\s+Data\s+con\s+lai\s+cua\s+goi\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemainingDataSignature();

    [GeneratedRegex(
        @"\bQuy\s+khach\s+hien\s+khong\s+dang\s+ky\s+su\s+dung\s+goi\s+cuoc\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoPackageSignature();

    [GeneratedRegex(
        "[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđÀÁẠẢÃÂẦẤẬẨẪĂẰẮẶẲẴÈÉẸẺẼÊỀẾỆỂỄÌÍỊỈĨÒÓỌỎÕÔỒỐỘỔỖƠỜỚỢỞỠÙÚỤỦŨƯỪỨỰỬỮỲÝỴỶỸĐ]")]
    private static partial Regex VietnameseDiacritic();
}
