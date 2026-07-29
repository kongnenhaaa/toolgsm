using System;
namespace gsm.Services;

public static class ImeiManagementService
{
    public static string GetDeviceNameFromImei(string imei)
    {
        if (string.IsNullOrWhiteSpace(imei) || imei.Length < 8) return "Mặc định (GSM Modem)";
        
        string tac = imei.Substring(0, 8);
        return tac switch
        {
            // --- SAMSUNG FLAGSHIPS & A-SERIES (Ưu tiên hàng đầu) ---
            "35414838" => "Samsung Galaxy S24 Ultra",
            "35414738" => "Samsung Galaxy S24+",
            "35414638" => "Samsung Galaxy S24",
            "35898337" => "Samsung Galaxy Z Fold 5",
            "35898237" => "Samsung Galaxy Z Flip 5",
            "35689020" => "Samsung Galaxy S23 Ultra",
            "35198031" => "Samsung Galaxy S23",
            "35205562" => "Samsung Galaxy S22 Ultra",
            "35848511" => "Samsung Galaxy S21 Ultra 5G",
            "35623011" => "Samsung Galaxy Note 20 Ultra",
            "35179311" => "Samsung Galaxy Z Fold 4",
            "35385711" => "Samsung Galaxy Z Flip 4",
            "35882911" => "Samsung Galaxy Z Fold 3",
            "35882811" => "Samsung Galaxy Z Flip 3",
            "35284911" => "Samsung Galaxy A54 5G",
            "35839211" => "Samsung Galaxy A53 5G",
            "35728411" => "Samsung Galaxy S20 FE 5G",
            "35184911" => "Samsung Galaxy A73 5G",
            "35682911" => "Samsung Galaxy A52s 5G",
            "35392811" => "Samsung Galaxy A34 5G",
            "35918211" => "Samsung Galaxy S21+ 5G",
            "35619211" => "Samsung Galaxy S20 Ultra 5G",
            "35489211" => "Samsung Galaxy Note 10+",
            "35298111" => "Samsung Galaxy M54 5G",
            "35192811" => "Samsung Galaxy A71 5G",

            // --- APPLE IPHONE FLAGSHIPS ---
            "35919376" => "iPhone 15 Pro Max",
            "35443477" => "iPhone 15 Pro",
            "35874288" => "iPhone 15",
            "35684784" => "iPhone 15 Plus",
            "35293630" => "iPhone 14 Pro Max",
            "35307371" => "iPhone 14",
            "35293425" => "iPhone 14 Pro",
            "35398226" => "iPhone 13 Pro Max",
            "35300911" => "iPhone 12 Pro Max",
            "35384110" => "iPhone 11 Pro Max",

            // --- GOOGLE PIXEL & ANDROID FLAGSHIPS ---
            "35424597" => "Google Pixel 8 Pro",
            "35639611" => "Google Pixel 7 Pro",
            "35824511" => "Google Pixel 6 Pro",
            "86884206" => "Xiaomi 14 Ultra",
            "86129004" => "Xiaomi 13 Pro",
            "86498205" => "Xiaomi 12 Pro",
            "86770205" => "Oppo Find X5 Pro",
            "86542704" => "Oppo Find X3 Pro",
            "86333405" => "Oppo Reno 8 Pro",
            "86744805" => "Vivo X90 Pro",
            "86086705" => "Huawei Mate 50 Pro",
            "35789211" => "Sony Xperia 1 V",
            "35289311" => "Asus ROG Phone 7",

            // Legacy / Older generated TACs from previous versions
            "35435973" => "Samsung Galaxy S23 Ultra (Cũ)",
            "35925411" => "iPhone 12",
            "35483211" => "Samsung Galaxy S21",
            "35832011" => "iPhone 13",
            "35303609" => "iPhone X",
            "86940804" => "Xiaomi Redmi Note 10",
            _ => "Mặc định (GSM Modem)"
        };
    }

    public static bool IsValidImei(string? imei)
    {
        string clean = NormalizeImeiValue(imei);
        if (clean.Length != 15) return false;

        int sum = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            int digit = clean[i] - '0';
            if ((i & 1) != 0)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        return sum % 10 == 0;
    }

    /// <summary>
    /// So sánh danh tính thiết bị theo TAC + SNR (14 số). Theo 3GPP, check digit
    /// không được truyền lên mạng và vị trí cuối có thể được biểu diễn bằng spare digit 0.
    /// Chỉ chấp nhận khác biệt này khi một phía là IMEI Luhn hợp lệ.
    /// </summary>
    public static bool AreEquivalentImei(string? left, string? right)
    {
        string a = NormalizeImeiValue(left);
        string b = NormalizeImeiValue(right);
        if (a.Length != 15 || b.Length != 15) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        if (!a.AsSpan(0, 14).SequenceEqual(b.AsSpan(0, 14))) return false;

        return (IsValidImei(a) && b[14] == '0')
            || (IsValidImei(b) && a[14] == '0');
    }

    public static bool IsUsableObservedImei(string? imei)
    {
        string clean = NormalizeImeiValue(imei);
        if (IsValidImei(clean)) return true;
        if (clean.Length != 15 || clean[14] != '0') return false;

        string canonical = clean[..14] + CalculateCheckDigit(clean[..14]);
        return IsValidImei(canonical);
    }

    public static string ToCanonicalImei(string? imei)
    {
        string clean = NormalizeImeiValue(imei);
        if (IsValidImei(clean)) return clean;
        if (clean.Length == 15 && clean[14] == '0')
        {
            int checkDigit = CalculateCheckDigit(clean[..14]);
            if (checkDigit >= 0) return clean[..14] + checkDigit;
        }
        return clean;
    }

    private static int CalculateCheckDigit(string first14Digits)
    {
        if (first14Digits.Length != 14 || first14Digits.Any(c => !char.IsDigit(c))) return -1;
        int sum = 0;
        for (int i = 0; i < first14Digits.Length; i++)
        {
            int digit = first14Digits[i] - '0';
            if ((i & 1) != 0)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        return (10 - (sum % 10)) % 10;
    }

    private static string NormalizeImeiValue(string? imei) => string.IsNullOrWhiteSpace(imei)
        ? string.Empty
        : new string(imei.Where(char.IsDigit).ToArray());

}
