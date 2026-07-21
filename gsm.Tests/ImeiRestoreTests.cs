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
        string modemImei2 = currentImei;

        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal))
                {
                    modemImei = canonicalBackupImei;
                    return Task.FromResult("OK");
                }
                if (command.StartsWith("AT+EGMR=1,10,", StringComparison.Ordinal))
                {
                    modemImei2 = canonicalBackupImei;
                    return Task.FromResult("OK");
                }

                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 0\r\nOK",
                    "AT+CGSN" => modemImei + "\r\nOK",
                    "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    "AT+EGMR=0,10" => $"+EGMR: \"{modemImei2}\"\r\nOK",
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
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+CFUN=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_DoesNotSucceedWhenCgsnMatchesButStoredImeiIsDifferent()
    {
        const string ccid = "89840200011750541177";
        const string currentImei = "867702058238604";
        const string targetImei = "355008370781449";
        const string wrongStoredImei = "351488165212715";

        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                "AT+CFUN?" => "+CFUN: 0\r\nOK",
                "AT+CGSN" => targetImei + "\r\nOK",
                "AT+EGMR=0,7" => $"+EGMR: \"{wrongStoredImei}\"\r\nOK",
                "AT+EGMR=0,10" => $"+EGMR: \"{targetImei}\"\r\nOK",
                _ => "OK"
            })
        };
        var service = new ImeiManagementService(modem);
        var port = new SimPort { PortName = "COM41", Serial = ccid, Imei = currentImei };
        var backup = new SimBackupEntry { Ccid = ccid, Imei = targetImei };
        var settings = new AppSettings { EnableImeiRestore = true, EnableNewSimIntakeMode = true };

        ImeiProcessResult result = await service.ProcessImeiAsync(
            port,
            ccid,
            currentImei,
            settings,
            _ => backup,
            _ => { },
            action => action(),
            validateIdentityAsync: () => Task.FromResult(true));

        Assert.Equal(ImeiProcessStatus.SecurityBlocked, result.Status);
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+CFUN=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_FailsClosedWhenSlot10CannotBeWritten()
    {
        const string ccid = "89840200011750541177";
        const string currentImei = "867702058238604";
        const string targetImei = "355008370781449";
        string modemImei = currentImei;

        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal))
                {
                    modemImei = targetImei;
                    return Task.FromResult("OK");
                }
                if (command.StartsWith("AT+EGMR=1,10,", StringComparison.Ordinal))
                    return Task.FromResult("ERROR");

                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 0\r\nOK",
                    "AT+CGSN" => modemImei + "\r\nOK",
                    "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    "AT+EGMR=0,10" => $"+EGMR: \"{currentImei}\"\r\nOK",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);
        var port = new SimPort { PortName = "COM42", Serial = ccid, Imei = currentImei };
        var backup = new SimBackupEntry { Ccid = ccid, Imei = targetImei };

        ImeiProcessResult result = await service.ProcessImeiAsync(
            port,
            ccid,
            currentImei,
            new AppSettings { EnableImeiRestore = true, EnableNewSimIntakeMode = true },
            _ => backup,
            _ => { },
            action => action(),
            validateIdentityAsync: () => Task.FromResult(true));

        Assert.Equal(ImeiProcessStatus.SecurityBlocked, result.Status);
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+CFUN=1", StringComparison.Ordinal));
    }
}
