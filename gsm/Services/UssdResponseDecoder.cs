using System.Text;
using System.Text.RegularExpressions;

namespace gsm.Services;

/// <summary>
/// Chuẩn hóa phản hồi USSD để mọi luồng chỉ đưa nội dung đã giải mã lên UI.
/// </summary>
public static partial class UssdResponseDecoder
{
    [GeneratedRegex(@"\+CUSD:\s*\d+\s*,\s*""(?<payload>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex CusdPayloadRegex();

    public static string Normalize(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return string.Empty;

        string trimmed = response.Trim();
        Match match = CusdPayloadRegex().Match(trimmed);
        return match.Success
            ? DecodePayload(match.Groups["payload"].Value)
            : trimmed;
    }

    public static string DecodePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return string.Empty;

        string value = payload.Trim();
        if (!LooksLikeUcs2(value))
            return value;

        try
        {
            return Encoding.BigEndianUnicode.GetString(Convert.FromHexString(value)).TrimEnd('\0');
        }
        catch (FormatException)
        {
            return value;
        }
    }

    private static bool LooksLikeUcs2(string value)
    {
        // "1601" có thể là số dư thật, không được hiểu nhầm thành một mã Unicode.
        if (value.Length < 8 || value.Length % 4 != 0 || !value.All(Uri.IsHexDigit))
            return false;

        int codeUnits = value.Length / 4;
        int printableAscii = 0;
        for (int i = 0; i < value.Length; i += 4)
        {
            int codeUnit = Convert.ToInt32(value.Substring(i, 4), 16);
            if (codeUnit is >= 0x20 and <= 0x7E || codeUnit is '\r' or '\n' or '\t')
                printableAscii++;
        }

        return printableAscii * 10 >= codeUnits * 3;
    }
}
