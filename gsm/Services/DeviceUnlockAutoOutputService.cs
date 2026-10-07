using gsm.Models;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Drawing;
using System.IO;

namespace gsm.Services;

public sealed record DeviceUnlockAutoOutputAppendResult(
    string FilePath,
    int RowCount,
    bool WasReset);

/// <summary>
/// Persists DKTTTB results across runs without retaining an unbounded history
/// in memory. Once the workbook contains 500 data rows, the next result starts
/// a fresh workbook.
/// </summary>
public sealed class DeviceUnlockAutoOutputService
{
    public const int MaximumRows = 500;
    public const string FileName = "DKTTTB_output.xlsx";
    private const string WorksheetName = "Ket qua DKTTTB";
    private static readonly string[] Headers =
    [
        "STT", "Thời gian", "Cổng", "SĐT", "Họ tên", "Trạng thái", "Phản hồi"
    ];

    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly int _maximumRows;

    public DeviceUnlockAutoOutputService()
        : this(AppPaths.ForUserDataFile(FileName), MaximumRows)
    {
    }

    internal DeviceUnlockAutoOutputService(string filePath, int maximumRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (maximumRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        _filePath = Path.GetFullPath(filePath);
        _maximumRows = maximumRows;
    }

    public string OutputPath => _filePath;

    public DeviceUnlockAutoOutputAppendResult Append(
        DeviceUnlockResultItem result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            string directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);

            bool wasReset = false;
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            ExcelPackage package;
            try
            {
                package = File.Exists(_filePath) && new FileInfo(_filePath).Length > 0
                    ? new ExcelPackage(new FileInfo(_filePath))
                    : new ExcelPackage();
            }
            catch
            {
                package = new ExcelPackage();
                wasReset = true;
            }

            byte[] workbookBytes;
            int rowCount;
            using (package)
            {
                ExcelWorksheet? sheet = package.Workbook.Worksheets[WorksheetName];
                int existingRows = sheet?.Dimension is null
                    ? 0
                    : Math.Max(0, sheet.Dimension.End.Row - 1);
                bool invalidHeader = sheet?.Dimension is not null
                    && !string.Equals(sheet.Cells[1, 1].Text, Headers[0],
                        StringComparison.Ordinal);

                if (sheet is not null &&
                    (existingRows >= _maximumRows || invalidHeader))
                {
                    package.Workbook.Worksheets.Delete(sheet.Name);
                    sheet = null;
                    wasReset = true;
                    existingRows = 0;
                }

                sheet ??= CreateWorksheet(package);
                int row = existingRows + 2;
                WriteResultRow(sheet, row, existingRows + 1, result);
                rowCount = existingRows + 1;
                sheet.Cells[1, 1, row, Headers.Length].AutoFilter = true;
                workbookBytes = package.GetAsByteArray();
            }

            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(_filePath)!,
                $"{Path.GetFileNameWithoutExtension(_filePath)}.{Guid.NewGuid():N}.tmp.xlsx");
            try
            {
                File.WriteAllBytes(temporaryPath, workbookBytes);
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch
                {
                }
            }

            return new DeviceUnlockAutoOutputAppendResult(
                _filePath,
                rowCount,
                wasReset);
        }
    }

    private static ExcelWorksheet CreateWorksheet(ExcelPackage package)
    {
        ExcelWorksheet sheet = package.Workbook.Worksheets.Add(WorksheetName);
        for (int column = 0; column < Headers.Length; column++)
            sheet.Cells[1, column + 1].Value = Headers[column];

        using (ExcelRange header = sheet.Cells[1, 1, 1, Headers.Length])
        {
            header.Style.Font.Bold = true;
            header.Style.Font.Color.SetColor(Color.White);
            header.Style.Fill.PatternType = ExcelFillStyle.Solid;
            header.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(37, 99, 235));
            header.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        }

        sheet.View.FreezePanes(2, 1);
        sheet.Column(1).Width = 7;
        sheet.Column(2).Width = 20;
        sheet.Column(3).Width = 12;
        sheet.Column(4).Width = 16;
        sheet.Column(5).Width = 28;
        sheet.Column(6).Width = 24;
        sheet.Column(7).Width = 70;
        sheet.Column(7).Style.WrapText = true;
        return sheet;
    }

    private static void WriteResultRow(
        ExcelWorksheet sheet,
        int row,
        int sequence,
        DeviceUnlockResultItem result)
    {
        sheet.Cells[row, 1].Value = sequence;
        sheet.Cells[row, 2].Value = result.Time;
        sheet.Cells[row, 2].Style.Numberformat.Format = "dd/mm/yyyy hh:mm:ss";
        sheet.Cells[row, 3].Value = result.Port;
        sheet.Cells[row, 4].Value = result.Phone;
        sheet.Cells[row, 4].Style.Numberformat.Format = "@";
        sheet.Cells[row, 5].Value = result.FullName;
        sheet.Cells[row, 6].Value = DeviceUnlockExcelExportService.GetStatus(result);
        sheet.Cells[row, 7].Value = result.Response;

        Color rowColor = result.Success
            ? Color.FromArgb(240, 253, 244)
            : Color.FromArgb(254, 242, 242);
        using ExcelRange dataRow = sheet.Cells[row, 1, row, Headers.Length];
        dataRow.Style.Fill.PatternType = ExcelFillStyle.Solid;
        dataRow.Style.Fill.BackgroundColor.SetColor(rowColor);
        dataRow.Style.VerticalAlignment = ExcelVerticalAlignment.Top;
    }
}
