using System;
using System.Text.RegularExpressions;

class Program
{
    static void Main()
    {
        string cleanContent = "(Zalo) Vui long KHONG CHIA SE cho ai hay website nao khac vi se MAT TAI KHOAN. Day la ma xac thuc OTP cho SDT (***687): 844542";
        string textForOtp = Regex.Replace(cleanContent, @"\*+\d+", "");
        Console.WriteLine("textForOtp: " + textForOtp);
        var otpMatch = Regex.Match(textForOtp, @"(?:mã|code|otp|là|la|zalo|viber|telegram|facebook|google|apple|tiktok|tinder)\s*(?:cho\s+sdt\s*(?:\(\))?)?\s*[:\-]?\s*(\d{4,8})", RegexOptions.IgnoreCase);
        if (!otpMatch.Success)
        {
            otpMatch = Regex.Match(textForOtp, @"(?<![\w:/])(?!1900|1800)\b(\d{4,8})\b(?![\w:/])", RegexOptions.IgnoreCase);
        }
        Console.WriteLine("Success: " + otpMatch.Success);
        if (otpMatch.Success)
        {
            string extractedOtp = otpMatch.Groups.Count > 1 && !string.IsNullOrEmpty(otpMatch.Groups[1].Value) ? otpMatch.Groups[1].Value : otpMatch.Value;
            Console.WriteLine("OTP: " + extractedOtp);
        }
    }
}
