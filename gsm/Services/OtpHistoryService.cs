using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace gsm.Services;

/// <summary>
/// Lưu lịch sử OTP vào file CSV, tự động xóa bản ghi cũ hơn 10 ngày.
/// </summary>
public static class OtpHistoryService
{
    private static readonly string _csvPath = AppPaths.ForRuntimeFile("otp_history.csv");
    private static readonly object _lock = new object();

    static OtpHistoryService()
    {
        // Tắt tạo file otp_history.csv theo yêu cầu
    }

    /// <summary>
    /// Thêm một bản ghi OTP mới và tự động dọn dẹp bản ghi cũ hơn 10 ngày.
    /// </summary>
    public static void Append(string port, string simPhone, string sender, string otp, string content)
    {
        // Đã tắt lưu lịch sử OTP vào file
    }

    /// <summary>
    /// Trả về N bản ghi OTP gần nhất (dùng cho REST API).
    /// </summary>
    public static List<OtpRecord> GetRecent(int count = 50)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_csvPath)) return new List<OtpRecord>();

                return File.ReadAllLines(_csvPath, Encoding.UTF8)
                    .Skip(1) // Bỏ header
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(ParseLine)
                    .Where(r => r != null)
                    .Reverse()
                    .Take(count)
                    .ToList()!;
            }
            catch
            {
                return new List<OtpRecord>();
            }
        }
    }

    private static void PurgeOldRecords()
    {
        try
        {
            if (!File.Exists(_csvPath)) return;

            var lines = File.ReadAllLines(_csvPath, Encoding.UTF8).ToList();
            if (lines.Count <= 1) return; // Chỉ có header

            var cutoff = DateTime.Now.AddDays(-10);
            var kept = new List<string> { lines[0] }; // Giữ header

            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = SplitCsvLine(line);
                if (parts.Length > 0 && DateTime.TryParse(parts[0], out var ts) && ts >= cutoff)
                    kept.Add(line);
            }

            File.WriteAllLines(_csvPath, kept, Encoding.UTF8);
        }
        catch { }
    }

    private static OtpRecord? ParseLine(string line)
    {
        try
        {
            var parts = SplitCsvLine(line);
            if (parts.Length < 6) return null;
            return new OtpRecord
            {
                Timestamp = parts[0],
                Port      = parts[1],
                SimPhone  = parts[2],
                Sender    = parts[3],
                Otp       = parts[4],
                Content   = parts[5]
            };
        }
        catch { return null; }
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current  = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Nếu chứa dấu phẩy, xuống dòng hoặc ngoặc kép → bọc trong ngoặc kép
        if (value.Contains(',') || value.Contains('\n') || value.Contains('"'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}

public class OtpRecord
{
    public string Timestamp { get; set; } = string.Empty;
    public string Port      { get; set; } = string.Empty;
    public string SimPhone  { get; set; } = string.Empty;
    public string Sender    { get; set; } = string.Empty;
    public string Otp       { get; set; } = string.Empty;
    public string Content   { get; set; } = string.Empty;
}
