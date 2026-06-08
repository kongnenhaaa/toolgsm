using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using Vosk;

namespace gsm.Services;

public class SpeechToTextService
{
    private const string ModelUrl = "https://alphacephei.com/vosk/models/vosk-model-vn-0.4.zip";
    private readonly string _modelFolder;
    private readonly string _basePath;
    private bool _isModelReady = false;
    private Model? _model;
    
    public event EventHandler<string>? LogMessage;

    public SpeechToTextService()
    {
        _basePath = AppDomain.CurrentDomain.BaseDirectory;
        _modelFolder = Path.Combine(_basePath, "vosk-model-vn-0.4");
        Vosk.Vosk.SetLogLevel(-1); // Tắt log rác của Vosk
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (!Directory.Exists(_modelFolder))
            {
                LogMessage?.Invoke(this, "Đang tải mô hình Vosk tiếng Việt bản đầy đủ (khoảng 78MB) để tăng độ chính xác...");
                await DownloadAndExtractModelAsync();
                LogMessage?.Invoke(this, "Tải và giải nén mô hình AI hoàn tất.");
            }

            _model = new Model(_modelFolder);
            _isModelReady = true;
            LogMessage?.Invoke(this, "Khởi tạo Speech-to-Text Tiếng Việt thành công.");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, $"Lỗi khởi tạo Speech-to-Text: {ex.Message}");
            _isModelReady = false;
        }
    }

    private async Task DownloadAndExtractModelAsync()
    {
        string zipPath = Path.Combine(_basePath, "vosk-model.zip");
        
        using (var client = new HttpClient())
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            var response = await client.GetAsync(ModelUrl);
            response.EnsureSuccessStatusCode();

            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs);
            }
        }

        if (Directory.Exists(_modelFolder))
        {
            Directory.Delete(_modelFolder, true);
        }

        ZipFile.ExtractToDirectory(zipPath, _basePath);
        File.Delete(zipPath); // Xóa file zip cho nhẹ máy
    }

    public string RecognizeWavFile(string wavFilePath)
    {
        if (!_isModelReady || _model == null)
        {
            return "Lỗi: Mô hình AI chưa sẵn sàng.";
        }

        if (!File.Exists(wavFilePath))
        {
            return "Lỗi: Không tìm thấy file ghi âm.";
        }

        try
        {
            using var recognizer = new VoskRecognizer(_model, 16000.0f);
            using var stream = new FileStream(wavFilePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(stream);

            // Bỏ qua header của file WAV (thường là 44 bytes đầu tiên)
            stream.Seek(44, SeekOrigin.Begin);

            byte[] buffer = new byte[4096];
            int bytesRead;

            while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                recognizer.AcceptWaveform(buffer, bytesRead);
            }

            // Parse JSON từ Vosk
            var resultJson = recognizer.FinalResult();
            
            // Extract the "text" field simply
            var match = System.Text.RegularExpressions.Regex.Match(resultJson, @"""text""\s*:\s*""([^""]+)""");
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            return "";
        }
        catch (Exception ex)
        {
            return $"Lỗi nhận diện giọng nói: {ex.Message}";
        }
    }
}
