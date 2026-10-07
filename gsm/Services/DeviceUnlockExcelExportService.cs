using gsm.Models;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Drawing;
using System.IO;

namespace gsm.Services;

public static class DeviceUnlockExcelExportService
{
    private static readonly string[] Headers =
    [
        "STT",
        "Thời gian",
        "Cổng",
        "SĐT",
        "Họ tên",
        "Trạng thái",
        "Phản hồi"
    ];

    public static void Export(
        string filePath,
        IReadOnlyList<DeviceUnlockResultItem> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
            throw new InvalidOperationException(
                "Phiên chạy gần nhất chưa có kết quả để xuất.");

        string fullPath = Path.GetFullPath(filePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        ExcelWorksheet sheet = package.Workbook.Worksheets.Add("Ket qua DKTTTB");

        for (int column = 0; column < Headers.Length; column++)
            sheet.Cells[1, column + 1].Value = Headers[column];

        using (ExcelRange header = sheet.Cells[1, 1, 1, Headers.Length])
        {
            header.Style.Font.Bold = true;
            header.Style.Font.Color.SetColor(Color.White);
            header.Style.Fill.PatternType = ExcelFillStyle.Solid;
            header.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(37, 99, 235));
            header.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            header.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
        }

        for (int index = 0; index < results.Count; index++)
        {
            DeviceUnlockResultItem result = results[index];
            int row = index + 2;
            sheet.Cells[row, 1].Value = index + 1;
            sheet.Cells[row, 2].Value = result.Time;
            sheet.Cells[row, 2].Style.Numberformat.Format = "dd/mm/yyyy hh:mm:ss";
            sheet.Cells[row, 3].Value = result.Port;
            sheet.Cells[row, 4].Value = result.Phone;
            sheet.Cells[row, 4].Style.Numberformat.Format = "@";
            sheet.Cells[row, 5].Value = result.FullName;
            sheet.Cells[row, 6].Value = GetStatus(result);
            sheet.Cells[row, 7].Value = result.Response;

            Color rowColor = result.Success
                ? Color.FromArgb(240, 253, 244)
                : Color.FromArgb(254, 242, 242);
            using ExcelRange dataRow = sheet.Cells[row, 1, row, Headers.Length];
            dataRow.Style.Fill.PatternType = ExcelFillStyle.Solid;
            dataRow.Style.Fill.BackgroundColor.SetColor(rowColor);
            dataRow.Style.VerticalAlignment = ExcelVerticalAlignment.Top;
        }

        ExcelRange table = sheet.Cells[1, 1, results.Count + 1, Headers.Length];
        table.AutoFilter = true;
        table.Style.Border.Top.Style = ExcelBorderStyle.Thin;
        table.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
        table.Style.Border.Left.Style = ExcelBorderStyle.Thin;
        table.Style.Border.Right.Style = ExcelBorderStyle.Thin;
        table.Style.Border.Top.Color.SetColor(Color.FromArgb(226, 232, 240));
        table.Style.Border.Bottom.Color.SetColor(Color.FromArgb(226, 232, 240));
        table.Style.Border.Left.Color.SetColor(Color.FromArgb(226, 232, 240));
        table.Style.Border.Right.Color.SetColor(Color.FromArgb(226, 232, 240));

        sheet.View.FreezePanes(2, 1);
        sheet.Cells[sheet.Dimension.Address].AutoFitColumns();
        sheet.Column(1).Width = 7;
        sheet.Column(2).Width = Math.Max(sheet.Column(2).Width, 20);
        sheet.Column(4).Width = Math.Max(sheet.Column(4).Width, 15);
        sheet.Column(7).Width = Math.Min(Math.Max(sheet.Column(7).Width, 35), 80);
        sheet.Column(7).Style.WrapText = true;

        File.WriteAllBytes(fullPath, package.GetAsByteArray());
    }

    internal static string GetStatus(DeviceUnlockResultItem result) =>
        result.AlreadyCompleted
            ? "Đã hoàn thành trước đó"
            : result.Success
                ? "Thành công"
                : "Thất bại";
}
