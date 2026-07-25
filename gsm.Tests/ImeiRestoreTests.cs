using gsm.Models;
using gsm.Services;
using gsm.Tests.Fakes;

namespace gsm.Tests;

public sealed class ImeiRestoreTests
{
    [Fact]
    public async Task Create_RadioOffTimeout_IsRecoverableErrorNotSecurityBlock()
    {
        const string ccid = "89840200011750541177";
        const string currentImei = "867702058238604";
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(
                command == "AT+CFUN?" ? "ERROR: Timeout" : "ERROR: Timeout")
        };
        var service = new ImeiManagementService(modem);

        ImeiProcessResult result = await service.ProcessImeiAsync(
            new SimPort { PortName = "COM84", Serial = ccid, Imei = currentImei },
            ccid,
            currentImei,
            new AppSettings { EnableImeiRestore = true, EnableNewSimIntakeMode = true },
            _ => new SimBackupEntry { Ccid = ccid, Imei = "355008370781449" },
            _ => { },
            action => action(),
            forceAccept: true,
            validateIdentityAsync: () => Task.FromResult(true));

        Assert.Equal(ImeiProcessStatus.Error, result.Status);
        Assert.Equal(SecurityErrors.RadioOffFailed, result.ErrorMessage);
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+EGMR=1,7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSim_ReadbackTimeout_IsRecoverableErrorNotSecurityBlock()
    {
        const string currentImei = "352054261826334";
        const string targetImei = "355008370781449";
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                "AT+CFUN?" => "+CFUN: 4\r\nOK",
                "AT+EGMR=0,7;" => $"+EGMR: \"{currentImei}\"\r\nOK",
                "AT+EGMR=0,7" => "ERROR: Timeout",
                _ when command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal) => "ERROR: Timeout",
                _ => "OK"
            })
        };
        var service = new ImeiManagementService(modem);

        ImeiProcessResult result = await service.ProcessImeiWithoutSimAsync(
            new SimPort { PortName = "COM85", Imei = currentImei },
            targetImei,
            _ => true,
            action => action(),
            backupCurrentBeforeWrite: true);

        Assert.Equal(ImeiProcessStatus.Error, result.Status);
        Assert.DoesNotContain("WrongImei", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+CFUN=1,1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("OK", true)]
    [InlineData("ERROR: Timeout (Thiết bị không phản hồi OK/ERROR)", true)]
    [InlineData("ERROR: Port disconnected", true)]
    [InlineData("ERROR", false)]
    [InlineData("ERROR: Port not open", false)]
    [InlineData("ERROR: Timeout waiting for lock", false)]
    public void ResetResponse_DistinguishesRebootFromRealRejection(
        string response,
        bool expected)
    {
        Assert.Equal(expected, ImeiManagementService.IsResetAcceptedOrRebooting(response));
    }

    [Fact]
    public async Task NoSimCreate_ResetTimeoutAfterVerifiedWrite_ContinuesAsReboot()
    {
        const string previousImei = "352054261826334";
        const string targetImei = "355008370781449";
        string modemImei = previousImei;
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal))
                    modemImei = targetImei;

                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 4\r\nOK",
                    "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    "AT+CFUN=1,1" => "ERROR: Timeout (Thiết bị không phản hồi OK/ERROR)",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);

        ImeiProcessResult result = await service.ProcessImeiWithoutSimAsync(
            new SimPort { PortName = "COM82", Imei = previousImei },
            targetImei,
            _ => true,
            action => action(),
            backupCurrentBeforeWrite: true);

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.True(result.ModemResetRequested);
        Assert.Equal(targetImei, result.FinalImei);
    }

    [Fact]
    public async Task NoSimCreate_LostWriteAckButMatchingReadback_ContinuesAsSuccess()
    {
        const string previousImei = "352054261826334";
        const string targetImei = "355008370781449";
        string modemImei = previousImei;
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal))
                {
                    modemImei = targetImei;
                    return Task.FromResult("ERROR: Timeout (Thiết bị không phản hồi OK/ERROR)");
                }

                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 4\r\nOK",
                    "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);

        ImeiProcessResult result = await service.ProcessImeiWithoutSimAsync(
            new SimPort { PortName = "COM83", Imei = previousImei },
            targetImei,
            _ => true,
            action => action(),
            backupCurrentBeforeWrite: true);

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.True(result.ModemResetRequested);
        Assert.Equal(targetImei, result.FinalImei);
    }

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
    public async Task ExistingBackup_UsesCapturedSautoSlot7Sequence()
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
                    "AT+CFUN?" => "+CFUN: 4\r\nOK",
                    "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);
        var port = new SimPort { PortName = "COM40", Serial = ccid, Imei = currentImei };
        var backup = new SimBackupEntry { Ccid = ccid, Imei = storedBackupImei };
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
            _ => { },
            action => action(),
            validateIdentityAsync: () => Task.FromResult(true));

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.Equal(canonicalBackupImei, result.FinalImei);
        Assert.True(result.ModemResetRequested);
        Assert.Equal(canonicalBackupImei, modemImei);
        Assert.Equal(
        [
            "COM40:AT+CFUN=4",
            "COM40:AT+CFUN?",
            $"COM40:AT+EGMR=1,7,\"{canonicalBackupImei}\"",
            "COM40:AT+EGMR=0,7",
            "COM40:AT+CFUN?",
            "COM40:AT+CPMS?",
            "COM40:AT+CFUN=1,1"
        ], modem.Commands);
        Assert.Contains($"COM40:AT+EGMR=1,7,\"{canonicalBackupImei}\"", modem.Commands);
        Assert.Contains("COM40:AT+EGMR=0,7", modem.Commands);
        Assert.Contains("COM40:AT+CFUN=1,1", modem.Commands);
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+CGSN", StringComparison.Ordinal));
        Assert.DoesNotContain(modem.Commands, command => command.Contains("EGMR=1,10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_DoesNotResetWhenSlot7ReadbackIsDifferent()
    {
        const string ccid = "89840200011750541177";
        const string currentImei = "867702058238604";
        const string targetImei = "355008370781449";
        const string wrongStoredImei = "351488165212715";

        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) => Task.FromResult(command switch
            {
                "AT+CFUN?" => "+CFUN: 4\r\nOK",
                "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{wrongStoredImei}\"\r\nOK",
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
    public async Task NewImei_SavesOriginalBackupBeforeFirstModemWrite()
    {
        const string ccid = "89840200011750541177";
        const string originalImei = "352054261826334";
        string writtenImei = string.Empty;
        var events = new List<string>();
        SimBackupEntry? saved = null;
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                events.Add($"CMD:{command}");
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal))
                {
                    writtenImei = System.Text.RegularExpressions.Regex.Match(command, @"\d{15}").Value;
                    return Task.FromResult("OK");
                }
                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 4\r\nOK",
                    "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{writtenImei}\"\r\nOK",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);
        var port = new SimPort { PortName = "COM43", Serial = ccid, Imei = originalImei };

        ImeiProcessResult result = await service.ProcessImeiAsync(
            port,
            ccid,
            originalImei,
            new AppSettings { EnableImeiRestore = true, EnableNewSimIntakeMode = true },
            _ => saved,
            entry =>
            {
                saved = entry;
                events.Add($"SAVE:{entry.Imei}");
            },
            action => action(),
            forceAccept: true,
            validateIdentityAsync: () => Task.FromResult(true));

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.Equal(originalImei, saved?.Imei);
        Assert.Equal("imei_backup.xlsx", saved?.SourceFile);
        Assert.True(events.IndexOf($"SAVE:{originalImei}") < events.IndexOf("CMD:AT+CFUN=4"));
        Assert.NotEqual(originalImei, result.FinalImei);
    }

    [Fact]
    public async Task Restore_DoesNotUseSlot10OrCgsn()
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
                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 4\r\nOK",
                    "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
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

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.Contains("COM42:AT+CFUN=1,1", modem.Commands);
        Assert.DoesNotContain(modem.Commands, command => command.Contains("AT+CGSN", StringComparison.Ordinal));
        Assert.DoesNotContain(modem.Commands, command => command.Contains("EGMR=1,10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSimCreate_BackupsCurrentThenWritesVerifiesAndResets()
    {
        const string previousImei = "352054261826334";
        const string targetImei = "355008370781449";
        string modemImei = previousImei;
        string? savedImei = null;
        var events = new List<string>();
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                events.Add($"CMD:{command}");
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal))
                {
                    modemImei = targetImei;
                    return Task.FromResult("OK");
                }
                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 4\r\nOK",
                    "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);
        var port = new SimPort { PortName = "COM9", Imei = previousImei };

        ImeiProcessResult result = await service.ProcessImeiWithoutSimAsync(
            port,
            targetImei,
            current =>
            {
                savedImei = current;
                events.Add($"SAVE:{current}");
                return true;
            },
            action => action(),
            backupCurrentBeforeWrite: true);

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.Equal(previousImei, savedImei);
        Assert.Equal(targetImei, result.FinalImei);
        Assert.True(result.ModemResetRequested);
        Assert.True(port.IsRebooting);
        Assert.True(events.IndexOf($"SAVE:{previousImei}") < events.IndexOf($"CMD:AT+EGMR=1,7,\"{targetImei}\""));
        Assert.Equal(
        [
            "COM9:AT+CFUN=4",
            "COM9:AT+CFUN?",
            "COM9:AT+EGMR=0,7;",
            $"COM9:AT+EGMR=1,7,\"{targetImei}\"",
            "COM9:AT+EGMR=0,7",
            "COM9:AT+CFUN?",
            "COM9:AT+CPMS?",
            "COM9:AT+CFUN=1,1"
        ], modem.Commands);
    }

    [Fact]
    public async Task NoSimCreate_WritesEvenWhenBackupCannotBeSaved()
    {
        const string previousImei = "352054261826334";
        const string targetImei = "355008370781449";
        string modemImei = previousImei;
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal))
                    modemImei = targetImei;
                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 4\r\nOK",
                    "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);

        ImeiProcessResult result = await service.ProcessImeiWithoutSimAsync(
            new SimPort { PortName = "COM10", Imei = previousImei },
            targetImei,
            _ => false,
            action => action(),
            backupCurrentBeforeWrite: true);

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.Contains(modem.Commands, command => command.Contains($"AT+EGMR=1,7,\"{targetImei}\"", StringComparison.Ordinal));
        Assert.Contains(modem.Commands, command => command.Contains("AT+CFUN=1,1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAgain_OverwritesBackupWithImeiPresentBeforeWrite()
    {
        const string ccid = "89840200011750541177";
        const string previousGeneratedImei = "352054261826334";
        const string nextImei = "355008370781449";
        var backup = new SimBackupEntry { Ccid = ccid, Imei = "867702058238604" };
        string modemImei = previousGeneratedImei;
        var modem = new FakeGsmModemService
        {
            CommandHandler = (_, command) =>
            {
                if (command.StartsWith("AT+EGMR=1,7,", StringComparison.Ordinal)) modemImei = nextImei;
                return Task.FromResult(command switch
                {
                    "AT+CFUN?" => "+CFUN: 4\r\nOK",
                    "AT+EGMR=0,7;" or "AT+EGMR=0,7" => $"+EGMR: \"{modemImei}\"\r\nOK",
                    _ => "OK"
                });
            }
        };
        var service = new ImeiManagementService(modem);

        ImeiProcessResult result = await service.ProcessImeiAsync(
            new SimPort { PortName = "COM11", Serial = ccid, Imei = previousGeneratedImei },
            ccid,
            previousGeneratedImei,
            new AppSettings { EnableImeiRestore = true, EnableNewSimIntakeMode = true },
            _ => backup,
            entry => backup = entry,
            action => action(),
            forceAccept: true,
            validateIdentityAsync: () => Task.FromResult(true),
            explicitTargetImei: nextImei,
            overwriteBackupWithCurrentImei: true);

        Assert.Equal(ImeiProcessStatus.Applied, result.Status);
        Assert.Equal(previousGeneratedImei, backup.Imei);
        Assert.Equal(nextImei, result.FinalImei);
    }
}
