using System;
using System.Collections.Generic;
using System.IO;
using OfficeOpenXml;

namespace gsm.Services;

/// <summary>
/// Đọc file Excel (.xlsx) để lấy danh sách số điện thoại và nội dung SMS cần gửi hàng loạt.
/// Cột A: Số điện thoại | Cột B: Nội dung tin nhắn
/// </summary>
public static class BulkSmsService
{
    static BulkSmsService()
    {
        // EPPlus 5+ yêu cầu khai báo license khi dùng phi thương mại
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    public static List<(string Phone, string Content)> ReadFromExcel(string filePath)
    {
        var result = new List<(string, string)>();

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Không tìm thấy file Excel.", filePath);

        using var package = new ExcelPackage(new FileInfo(filePath));
        var sheet = package.Workbook.Worksheets[0]; // Sheet đầu tiên

        if (sheet == null)
            throw new Exception("File Excel không có sheet nào.");

        int rowCount = sheet.Dimension?.Rows ?? 0;

        for (int row = 2; row <= rowCount; row++) // Bắt đầu từ dòng 2 (bỏ header)
        {
            string? phone   = sheet.Cells[row, 1].Text?.Trim();
            string? content = sheet.Cells[row, 2].Text?.Trim();

            if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(content))
                continue;

            // Chuẩn hóa số điện thoại: nếu bắt đầu bằng 84 thì đổi thành 0
            if (phone.StartsWith("+84")) phone = "0" + phone[3..];
            else if (phone.StartsWith("84") && phone.Length >= 11) phone = "0" + phone[2..];

            result.Add((phone, content));
        }

        return result;
    }
}
