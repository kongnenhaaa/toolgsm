using System.Reflection;
using gsm.Models;
using gsm.Services;

namespace gsm.Tests;

public sealed class FirebaseSyncSettingsTests
{
    [Fact]
    public void NewSettings_DefaultFirebaseSyncToOff()
    {
        var settings = new AppSettings();

        Assert.False(settings.WriteOtpToFirebase);
    }

    [Fact]
    public void LegacySettings_MigrateOnceToOff_ThenPreserveExplicitChoice()
    {
        MethodInfo normalize = typeof(SettingsService).GetMethod(
            "Normalize",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Settings normalization was not found.");

        var legacy = new AppSettings
        {
            WriteOtpToFirebase = true,
            FirebaseSyncPreferenceVersion = 0
        };
        var migrated = (AppSettings)normalize.Invoke(null, [legacy])!;
        Assert.False(migrated.WriteOtpToFirebase);
        Assert.Equal(1, migrated.FirebaseSyncPreferenceVersion);

        migrated.WriteOtpToFirebase = true;
        var explicitlyEnabled = (AppSettings)normalize.Invoke(null, [migrated])!;
        Assert.True(explicitlyEnabled.WriteOtpToFirebase);
        Assert.Equal(1, explicitlyEnabled.FirebaseSyncPreferenceVersion);
    }

    [Fact]
    public void GoogleSheetTargets_AreTrimmedAndReceiveUniqueIds()
    {
        MethodInfo normalize = typeof(SettingsService).GetMethod(
            "Normalize",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Settings normalization was not found.");
        var settings = new AppSettings
        {
            GoogleSheetsWebAppUrl = " https://script.google.com/macros/s/test/exec ",
            GoogleSheetsSharedSecret = " secret ",
            GoogleSheetTargets =
            [
                new GoogleSheetTargetSettings
                {
                    Id = "same-id",
                    Name = " Kho 1 ",
                    SpreadsheetUrl = " https://docs.google.com/spreadsheets/d/abc/edit ",
                    WorksheetName = " Data "
                },
                new GoogleSheetTargetSettings
                {
                    Id = "same-id",
                    Name = "Kho 2",
                    SpreadsheetUrl = "https://docs.google.com/spreadsheets/d/def/edit",
                    WorksheetName = ""
                },
                new GoogleSheetTargetSettings()
            ]
        };

        var normalized = (AppSettings)normalize.Invoke(null, [settings])!;

        Assert.Equal("https://script.google.com/macros/s/test/exec", normalized.GoogleSheetsWebAppUrl);
        Assert.Equal("secret", normalized.GoogleSheetsSharedSecret);
        Assert.Equal(2, normalized.GoogleSheetTargets.Count);
        Assert.Equal("Kho 1", normalized.GoogleSheetTargets[0].Name);
        Assert.Equal("Data", normalized.GoogleSheetTargets[0].WorksheetName);
        Assert.Equal("ToolGSM", normalized.GoogleSheetTargets[1].WorksheetName);
        Assert.NotEqual(
            normalized.GoogleSheetTargets[0].Id,
            normalized.GoogleSheetTargets[1].Id);
    }
}
