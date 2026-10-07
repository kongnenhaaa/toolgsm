using gsm.Models;
using gsm.Services;
using OfficeOpenXml;

namespace gsm.Tests;

public sealed class DeviceUnlockExcelExportServiceTests
{
    [Fact]
    public void Export_WritesOnlyProvidedSessionResultsWithExpectedColumns()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-device-unlock-excel-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "result.xlsx");
        try
        {
            DeviceUnlockResultItem[] results =
            [
                new()
                {
                    Time = new DateTime(2026, 8, 30, 8, 15, 20),
                    Port = "COM3",
                    Phone = "0912345678",
                    FullName = "Nguyễn Văn A",
                    Success = true,
                    Response = "Mở khóa đổi thiết bị thành công"
                },
                new()
                {
                    Time = new DateTime(2026, 8, 30, 8, 16, 20),
                    Port = "COM4",
                    Phone = "0987654321",
                    Success = false,
                    Response = "errorCode=1314"
                }
            ];

            DeviceUnlockExcelExportService.Export(path, results);

            Assert.True(File.Exists(path));
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(new FileInfo(path));
            ExcelWorksheet sheet = package.Workbook.Worksheets["Ket qua DKTTTB"];
            Assert.Equal("STT", sheet.Cells[1, 1].Text);
            Assert.Equal("SĐT", sheet.Cells[1, 4].Text);
            Assert.Equal("Phản hồi", sheet.Cells[1, 7].Text);
            Assert.Equal("0912345678", sheet.Cells[2, 4].Text);
            Assert.Equal("Thành công", sheet.Cells[2, 6].Text);
            Assert.Equal("Thất bại", sheet.Cells[3, 6].Text);
            Assert.Equal("errorCode=1314", sheet.Cells[3, 7].Text);
            Assert.Equal(3, sheet.Dimension.End.Row);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Export_RejectsEmptySession()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-empty-export-" + Guid.NewGuid().ToString("N") + ".xlsx");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => DeviceUnlockExcelExportService.Export(path, []));

        Assert.Contains("chưa có kết quả", error.Message);
        Assert.False(File.Exists(path));
    }
}
