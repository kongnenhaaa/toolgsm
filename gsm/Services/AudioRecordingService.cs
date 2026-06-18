using System;
using System.IO;
using System.Linq;
using NAudio.Wave;

namespace gsm.Services;

public class AudioRecordingService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string _currentFilePath = string.Empty;
    public bool IsRecording { get; private set; }

    public event EventHandler<string>? LogMessage;

    public AudioRecordingService()
    {
    }

    /// <summary>
    /// Tìm thiết bị Microphone của Quectel/USB. Nếu không thấy, lấy thiết bị mặc định (0).
    /// </summary>
    private int GetDeviceNumber()
    {
        int waveInDevices = WaveIn.DeviceCount;
        if (waveInDevices <= 0)
        {
            return -1;
        }

        for (int i = 0; i < waveInDevices; i++)
        {
            var deviceInfo = WaveIn.GetCapabilities(i);
            string name = deviceInfo.ProductName.ToLowerInvariant();
            if (name.Contains("quectel") || name.Contains("usb audio") || name.Contains("ec20") || name.Contains("modem"))
            {
                return i;
            }
        }

        return 0;
    }

    public void StartRecording(string portName)
    {
        if (IsRecording) return;

        try
        {
            string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);

            _currentFilePath = Path.Combine(logsDir, $"call_{portName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            int deviceNumber = GetDeviceNumber();
            if (deviceNumber < 0)
            {
                LogMessage?.Invoke(this, "Không tìm thấy thiết bị ghi âm audio input.");
                return;
            }

            var deviceInfo = WaveIn.GetCapabilities(deviceNumber);
            LogMessage?.Invoke(this, $"Bắt đầu ghi âm cuộc gọi trên thiết bị: {deviceInfo.ProductName}");

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 1) // 16kHz, Mono - Chuẩn bắt buộc của Vosk
            };

            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.RecordingStopped += WaveIn_RecordingStopped;

            _writer = new WaveFileWriter(_currentFilePath, _waveIn.WaveFormat);

            _waveIn.StartRecording();
            IsRecording = true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, $"Lỗi ghi âm: {ex.Message}");
            StopRecording();
        }
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_writer != null)
        {
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _writer.Flush();
        }
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRecording = false;
        DisposeWriter();
    }

    public string StopRecording()
    {
        if (!IsRecording && _writer == null) return _currentFilePath;

        try
        {
            _waveIn?.StopRecording();
            IsRecording = false;
        }
        catch { }

        DisposeWriter();
        return _currentFilePath;
    }

    private void DisposeWriter()
    {
        if (_writer != null)
        {
            try
            {
                _writer.Dispose();
            }
            catch { }
            _writer = null;
        }

        if (_waveIn != null)
        {
            try
            {
                _waveIn.Dispose();
            }
            catch { }
            _waveIn = null;
        }
    }

    public void Dispose()
    {
        StopRecording();
    }
}
