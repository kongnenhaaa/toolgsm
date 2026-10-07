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
}
