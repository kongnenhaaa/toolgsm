using System.Reflection;
using gsm.Models;
using gsm.Services;
using gsm.ViewModels;

namespace gsm.Tests;

public sealed class TelegramNotificationPreferenceTests
{
    [Fact]
    public void NewSettings_DefaultOtpAndSmsTelegramToOff()
    {
        var settings = new AppSettings();

        Assert.False(settings.TelegramOnOtp);
        Assert.False(settings.TelegramOnSms);
    }

    [Fact]
    public void LegacySettings_MigrateOnceToOff_ThenPreserveExplicitChoice()
    {
        MethodInfo normalize = typeof(SettingsService).GetMethod(
            "Normalize", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Settings normalization was not found.");

        var legacy = new AppSettings
        {
            TelegramOnOtp = true,
            TelegramOnSms = true,
            TelegramMessagePreferenceVersion = 0
        };
        var migrated = (AppSettings)normalize.Invoke(null, [legacy])!;
        Assert.False(migrated.TelegramOnOtp);
        Assert.False(migrated.TelegramOnSms);
        Assert.Equal(1, migrated.TelegramMessagePreferenceVersion);

        migrated.TelegramOnOtp = true;
        migrated.TelegramOnSms = true;
        var explicitlyEnabled = (AppSettings)normalize.Invoke(null, [migrated])!;
        Assert.True(explicitlyEnabled.TelegramOnOtp);
        Assert.True(explicitlyEnabled.TelegramOnSms);
    }

    [Theory]
    [InlineData(false, false, "123456", false)]
    [InlineData(false, false, "N/A", false)]
    [InlineData(true, false, "123456", true)]
    [InlineData(true, false, "N/A", false)]
    [InlineData(false, true, "123456", false)]
    [InlineData(false, true, "N/A", true)]
    public void SmsGate_RespectsOtpAndOrdinarySmsPreferences(
        bool otpEnabled, bool smsEnabled, string otp, bool expected)
    {
        var settings = new AppSettings
        {
            TelegramOnOtp = otpEnabled,
            TelegramOnSms = smsEnabled
        };

        Assert.Equal(expected,
            MainViewModel.ShouldSendTelegramSmsNotification(settings, otp));
        Assert.Equal(expected,
            NotifyService.IsTelegramNotificationEnabled(settings,
                otp == "N/A"
                    ? TelegramNotificationKind.Sms
                    : TelegramNotificationKind.Otp));
    }
}
