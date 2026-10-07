using gsm.Models;
using gsm.Services;
using OfficeOpenXml;

namespace gsm.Tests;

public sealed class DeviceUnlockAutoOutputServiceTests
{
    [Fact]
    public void Append_PersistsResultsAcrossServiceInstances()
    {
        (string directory, string path) = CreateOutputPath();
        try
        {
            var firstRun = new DeviceUnlockAutoOutputService(path, maximumRows: 500);
            firstRun.Append(Result("COM3", "0911111111", true));

            var secondRun = new DeviceUnlockAutoOutputService(path, maximumRows: 500);
            DeviceUnlockAutoOutputAppendResult appended =
                secondRun.Append(Result("COM4", "0922222222", false));

            Assert.False(appended.WasReset);
            Assert.Equal(2, appended.RowCount);
            using ExcelPackage package = Open(path);
            ExcelWorksheet sheet = package.Workbook.Worksheets["Ket qua DKTTTB"];
            Assert.Equal("0911111111", sheet.Cells[2, 4].Text);
            Assert.Equal("0922222222", sheet.Cells[3, 4].Text);
            Assert.Equal("Thất bại", sheet.Cells[3, 6].Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Append_ResetsWorkbookBeforeExceedingConfiguredLimit()
    {
        (string directory, string path) = CreateOutputPath();
        try
        {
            var output = new DeviceUnlockAutoOutputService(path, maximumRows: 3);
            output.Append(Result("COM1", "0911111111", true));
            output.Append(Result("COM2", "0922222222", true));
            output.Append(Result("COM3", "0933333333", true));

            DeviceUnlockAutoOutputAppendResult reset =
                output.Append(Result("COM4", "0944444444", false));

            Assert.True(reset.WasReset);
            Assert.Equal(1, reset.RowCount);
            using ExcelPackage package = Open(path);
            ExcelWorksheet sheet = package.Workbook.Worksheets["Ket qua DKTTTB"];
            Assert.Equal(2, sheet.Dimension.End.Row);
            Assert.Equal("COM4", sheet.Cells[2, 3].Text);
            Assert.Equal("0944444444", sheet.Cells[2, 4].Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Append_IsSafeWhenManyPortsFinishConcurrently()
    {
        (string directory, string path) = CreateOutputPath();
        try
        {
            var output = new DeviceUnlockAutoOutputService(path, maximumRows: 500);

            Parallel.For(0, 32, index =>
                output.Append(Result(
                    $"COM{index + 1}",
                    $"09{index:D8}",
                    success: index % 2 == 0)));

            using ExcelPackage package = Open(path);
            ExcelWorksheet sheet = package.Workbook.Worksheets["Ket qua DKTTTB"];
            Assert.Equal(33, sheet.Dimension.End.Row);
            string[] ports = Enumerable.Range(2, 32)
                .Select(row => sheet.Cells[row, 3].Text)
                .ToArray();
            Assert.Equal(32, ports.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DeviceUnlockResultItem Result(
        string port,
        string phone,
        bool success) => new()
    {
        Time = new DateTime(2026, 8, 30, 12, 0, 0),
        Port = port,
        Phone = phone,
        Success = success,
        Response = success ? "Mở khóa thành công" : "Lỗi hệ thống"
    };

    private static (string Directory, string Path) CreateOutputPath()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "toolgsm-auto-output-" + Guid.NewGuid().ToString("N"));
        return (directory, Path.Combine(directory, "DKTTTB_output.xlsx"));
    }

    private static ExcelPackage Open(string path)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        return new ExcelPackage(new FileInfo(path));
    }
}
