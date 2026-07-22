using gsm.Models;

namespace gsm.Tests;

public sealed class ComTableColumnCatalogTests
{
    [Fact]
    public void DefaultCatalog_HasUniqueColumnsInExpectedUiOrder()
    {
        string[] expectedNames =
        [
            "Stt", "PortName", "Status", "NetworkProvider", "NetworkType", "Signal",
            "LastSignalScan", "Balance", "PhoneNumber", "SimType", "Imei", "Serial",
            "ExpiryDate", "SimRegDate", "Lock1C", "Lock2C", "ForwardedTo", "VnptStatus",
            "Otp", "LastSmsSender", "LastMessageContent"
        ];

        Assert.Equal(expectedNames, ComTableColumnCatalog.Default.Select(column => column.Name));
        Assert.Equal(
            ComTableColumnCatalog.Default.Count,
            ComTableColumnCatalog.Default.Select(column => column.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(ComTableColumnCatalog.Default, column =>
        {
            Assert.False(string.IsNullOrWhiteSpace(column.Name));
            Assert.False(string.IsNullOrWhiteSpace(column.Header));
        });
    }
}
