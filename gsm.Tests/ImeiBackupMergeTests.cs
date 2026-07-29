using gsm.Models;
namespace gsm.Tests;

public sealed class ImeiBackupMergeTests
{
    [Fact]
    public void SimBackupEntry_ContainsOnlyCcidAndImei()
    {
        string[] properties = typeof(SimBackupEntry)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Ccid", "Imei"], properties);
    }

    [Fact]
    public void ModemBackupEntry_ContainsOnlyPortNameAndImei()
    {
        string[] properties = typeof(ModemImeiBackupEntry)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Imei", "PortName"], properties);
    }
}
