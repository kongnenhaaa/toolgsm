using System.Text.Json;
using gsm.Models;
using gsm.Services;

namespace gsm.Tests;

public sealed class FirebasePortSyncTests
{
    [Fact]
    public void PortSnapshot_IncludesRemoteImeiAndSimIdentity()
    {
        var port = new SimPort
        {
            PortName = "COM7",
            Imei = "123456789012345",
            Serial = "8984012345678901234",
            SimType = "4G",
            PhoneNumber = "0912345678",
            Status = SimStatus.Active
        };

        string json = FirebaseService.SerializePortsData([port], "May-GSM-1");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement snapshot = document.RootElement.GetProperty("COM7");
        Assert.Equal("123456789012345", snapshot.GetProperty("imei").GetString());
        Assert.Equal("8984012345678901234", snapshot.GetProperty("serial").GetString());
        Assert.Equal("4G", snapshot.GetProperty("simType").GetString());
        Assert.Equal("May-GSM-1", snapshot.GetProperty("machineId").GetString());
        Assert.Equal("online", snapshot.GetProperty("status").GetString());
    }
}
