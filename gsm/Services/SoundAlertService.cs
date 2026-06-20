using System;
using System.IO;
using System.Media;

namespace gsm.Services;

/// <summary>
/// Phát âm thanh cảnh báo khi nhận OTP, SMS, hoặc cuộc gọi đến.
/// Hỗ trợ file .wav tùy chỉnh hoặc âm thanh hệ thống Windows làm fallback.
/// </summary>
public static class SoundAlertService
{
    /// <summary>Phát âm khi nhận được OTP mới.</summary>
    public static void PlayOtp()
    {
        if (!SettingsService.Current.EnableSoundAlert) return;
        Play(SettingsService.Current.SoundOtpPath, SystemSounds.Exclamation);
    }

    /// <summary>Phát âm khi nhận tin nhắn SMS thông thường (không có OTP).</summary>
    public static void PlaySms()
    {
        if (!SettingsService.Current.EnableSoundAlert) return;
        Play(SettingsService.Current.SoundSmsPath, SystemSounds.Asterisk);
    }

    /// <summary>Phát âm khi có cuộc gọi đến.</summary>
    public static void PlayCall()
    {
        if (!SettingsService.Current.EnableSoundAlert) return;
        Play(SettingsService.Current.SoundCallPath, SystemSounds.Question);
    }

    private static void Play(string? filePath, SystemSound fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                // Phát file WAV tùy chỉnh (async, không block UI)
                var player = new SoundPlayer(filePath);
                player.Play();
            }
            else
            {
                // Fallback: dùng âm thanh hệ thống Windows
                fallback.Play();
            }
        }
        catch
        {
            // Im lặng nếu thiết bị không có âm thanh hoặc file lỗi
        }
    }
}
