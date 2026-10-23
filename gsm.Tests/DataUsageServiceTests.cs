using gsm.Models;
using gsm.Services;
using gsm.Tests.Fakes;

namespace gsm.Tests;

public class DataUsageServiceTests
{
    [Theory]
    [InlineData("452011234567890", "m-wap", "", 0)]
    [InlineData("452021234567890", "m3-world", "mms", 1)]
    [InlineData("452041234567890", "v-internet", "", 0)]
    [InlineData("452051234567890", "internet", "", 0)]
    public async Task EmptyApn_ConfiguresCarrierAndDownloadsExactBytes(
        string imsi,
        string apn,
        string user,
        int authentication)
    {
        FakeGsmModemService modem = CreateModem();
        modem.CommandHandler = (port, command) => Task.FromResult(command switch
        {
            "AT+QIACT?" => "OK",
            "AT+QICSGP=1" => "+QICSGP: 1,\"\",\"\",\"\",0\r\nOK",
            "AT+CIMI" => imsi + "\r\nOK",
            "AT+QHTTPURL=46,20" => "CONNECT",
            "http://speed.cloudflare.com/__down?bytes=51200" => "OK",
            "AT+QHTTPGET=80" => RaiseCompletedDownload(modem, port, 51200),
            _ => "OK"
        });

        DataUsageRunResult result = await new DataUsageService(modem).ConsumeAsync("COM1", 50);

        Assert.Equal(51200, result.ReportedBytes);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Contains("KB qua dữ liệu di động", result.Message);
        Assert.Contains(
            $"COM1:AT+QICSGP=1,1,\"{apn}\",\"{user}\",\"{user}\",{authentication}",
            modem.Commands);
        Assert.DoesNotContain(modem.Commands, command => command.Contains("QHTTPGETEX"));
        Assert.Equal(["COM1"], modem.BackgroundResumptions);
    }

    [Fact]
    public async Task ExistingApn_IsPreserved()
    {
        FakeGsmModemService modem = CreateModem();
        modem.CommandHandler = (port, command) => Task.FromResult(command switch
        {
            "AT+QIACT?" => "OK",
            "AT+QICSGP=1" => "+QICSGP: 1,\"custom.apn\",\"\",\"\",0\r\nOK",
            "AT+QHTTPURL=45,20" => "CONNECT",
            "AT+QHTTPGET=80" => RaiseCompletedDownload(modem, port, 1024),
            _ => "OK"
        });

        await new DataUsageService(modem).ConsumeAsync("COM1", 1);

        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+CIMI"));
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+QICSGP=1,1"));
    }

    [Fact]
    public async Task UnregisteredSim_ReportsNetworkStateBeforeActivatingPdp()
    {
        FakeGsmModemService modem = CreateModem();
        modem.CommandHandler = (_, command) => Task.FromResult(command switch
        {
            "AT+QIACT?" => "OK",
            "AT+CEREG?" => "+CEREG: 2,0\r\nOK",
            "AT+CGREG?" => "+CGREG: 2,0\r\nOK",
            _ => "OK"
        });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DataUsageService(modem).ConsumeAsync("COM1", 50));

        Assert.Contains("chưa đăng ký mạng dữ liệu", error.Message);
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+QIACT=1"));
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+QHTTPGET"));
    }

    [Theory]
    [InlineData(200, 1024)]
    [InlineData(200, null)]
    [InlineData(403, 51200)]
    public async Task WrongOrUnreportedDownload_IsNotMarkedSuccessful(int status, int? bytes)
    {
        FakeGsmModemService modem = CreateModem();
        modem.CommandHandler = (port, command) => Task.FromResult(command switch
        {
            "AT+QIACT?" => "+QIACT: 1,1,1,\"10.1.2.3\"\r\nOK",
            "AT+QHTTPURL=46,20" => "CONNECT",
            "AT+QHTTPGET=80" => RaiseCompletedDownload(modem, port, bytes, status),
            _ => "OK"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DataUsageService(modem).ConsumeAsync("COM1", 50));
    }

    private static FakeGsmModemService CreateModem() => new()
    {
        ModemProfile = QuectelModemProfile.FromIdentity(
            "Quectel", "EC20F", "EC20CEHDLGR08A05M1G")
    };

    private static string RaiseCompletedDownload(
        FakeGsmModemService modem,
        string port,
        int? bytes,
        int status = 200)
    {
        modem.RaiseLog(port, $"+QHTTPGET: 0,{status}{(bytes.HasValue ? "," + bytes.Value : "")}");
        return "OK";
    }
}
