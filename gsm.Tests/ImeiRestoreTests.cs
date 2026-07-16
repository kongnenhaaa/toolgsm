using gsm.Models;
using gsm.Services;
using gsm.Tests.Fakes;

namespace gsm.Tests;

public sealed class ImeiRestoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingBackup_WaitsForAccept_EvenWhenPortIsRebooting(bool isRebooting)
    {
        const string ccid = "89840200011759999999";
        const string currentImei = "352054261826334";
        var modem = new FakeGsmModemService();
        var service = new ImeiManagementService(modem);
        var port = new SimPort
        {
            PortName = "COM12",
            Serial = ccid,
            Imei = currentImei,
            IsRebooting = isRebooting
        };
        var settings = new AppSettings
        {
            EnableImeiRestore = true,
            EnableNewSimIntakeMode = true,
            AutoAccept = false
        };

        ImeiProcessResult result = await service.ProcessImeiAsync(
            port,
            ccid,
            currentImei,
            settings,
            _ => null,
            _ => throw new InvalidOperationException("Chưa ACCEPT thì không được tạo backup"),
            action => action(),
            validateIdentityAsync: () => Task.FromResult(true));

        Assert.Equal(ImeiProcessStatus.WaitingAccept, result.Status);
        Assert.Equal(currentImei, result.FinalImei);
        Assert.Empty(modem.Commands);
    }

    [Fact]
    public async Task ExistingBackup_IsCanonicalizedWrittenAndVerifiedThroughCgsnAndEgmr()
    {
        const string ccid = "89840200011750541177";
        const string currentImei = "867702058238604";
        const string storedBackupImei = "355008370781440";
        const string canonicalBackupImei = "355008370781449";
        string modemImei = currentImei;

        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal))
                {
                    modemImei = canonicalBackupImei;
                    return Task.FromResult("OK");
                }

                return Task.FromResult(command switch
                {
                    "AT+CGSN" => modemImei + "\r\nOK",
                    "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);
        var port = new SimPort { PortName = "COM40", Serial = ccid, Imei = currentImei };
        var backup = new SimBackupEntry { Ccid = ccid, Imei = storedBackupImei };
        SimBackupEntry? saved = null;
        var settings = new AppSettings
        {
            EnableImeiRestore = true,
            EnableNewSimIntakeMode = true
        };

        ImeiProcessResult result = await service.ProcessImeiAsync(
            port,
            ccid,
            currentImei,
            settings,
            _ => backup,
            entry => saved = entry,
            action => action(),
            validateIdentityAsync: () => Task.FromResult(true));

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.Equal(canonicalBackupImei, result.FinalImei);
        Assert.Equal(canonicalBackupImei, modemImei);
        Assert.Equal(canonicalBackupImei, saved?.Imei);
        Assert.Contains($"COM40:AT+EGMR=1,7,\"{canonicalBackupImei}\"", modem.Commands);
        Assert.Contains("COM40:AT+CGSN", modem.Commands);
        Assert.Contains("COM40:AT+EGMR=0,7", modem.Commands);
    }
}
