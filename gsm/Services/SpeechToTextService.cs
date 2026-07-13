using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using NAudio.Wave;
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
            using var reader = new WaveFileReader(wavFilePath);
            
            // AI Model yêu cầu 16kHz, nếu file Quectel là 8kHz thì phải Resample lên 16kHz
            var outFormat = new WaveFormat(16000, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, outFormat)
            {
                ResamplerQuality = 60
            };
            
            using var recognizer = new VoskRecognizer(_model, 16000.0f);
            
            byte[] buffer = new byte[4096];
            int bytesRead;

            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                recognizer.AcceptWaveform(buffer, bytesRead);
            }

            // Parse JSON từ Vosk
            var resultJson = recognizer.FinalResult();
            
            // Extract the "text" field simply
            var match = System.Text.RegularExpressions.Regex.Match(resultJson, @"""text""\s*:\s*""([^""]+)""");
            if (match.Success)
            {
                string text = match.Groups[1].Value.Trim();
                if (text.Contains("ngọ đã đổ ra cửa bám trên toàn ấn độ dương") || 
                    text == "một con ruồi" || 
                    text == "tôi" || 
                    text == "ông" || 
                    text == "thì" || 
                    text.Length <= 3)
                {
                    return "";
                }

                // Tối ưu hóa cho mã OTP: Chuyển đổi các chữ số tiếng Việt và các từ bị nhận diện sai phổ biến sang số nguyên
                var numberWords = new System.Collections.Generic.Dictionary<string, string>
                {
                    {"không", "0"}, {"khong", "0"},
                    {"một", "1"}, {"mốt", "1"}, {"mot", "1"}, {"mua", "1"},
                    {"hai", "2"}, {"ai", "2"}, {"ơi", "2"}, {"hơi", "2"},
                    {"ba", "3"}, {"ngờ", "3"}, {"to", "3"}, {"nợ", "3"},
                    {"bốn", "4"}, {"bon", "4"}, {"tốn", "4"}, {"tư", "4"}, {"trốn", "4"},
                    {"năm", "5"}, {"lăm", "5"}, {"nam", "5"}, {"lam", "5"}, {"ngũ", "5"}, {"lâm", "5"}, 
                    {"sáu", "6"}, {"sau", "6"}, {"sâu", "6"},
                    {"bảy", "7"}, {"bay", "7"}, {"mấy", "7"}, {"ý", "7"},
                    {"tám", "8"}, {"tam", "8"}, {"tả", "8"}, {"tấm", "8"}, {"tâm", "8"}, 
                    {"chín", "9"}, {"chin", "9"}, {"chính", "9"}
                };

                // Split into words, replace known number words
                string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < words.Length; i++)
                {
                    if (numberWords.TryGetValue(words[i].ToLower(), out string? digit) && digit != null)
                    {
                        words[i] = digit;
                    }
                }
                
                // Reconstruct the text
                string final_text = string.Join(" ", words);

                // Trích xuất CHỈ các con số (lọc bỏ toàn bộ chữ để bắt chuẩn xác chuỗi mã OTP)
                string digitsOnly = System.Text.RegularExpressions.Regex.Replace(final_text, @"[^0-9]", "");

                // Nếu không lấy được số nào, trả về chuỗi text đã được nắn sửa
                return !string.IsNullOrEmpty(digitsOnly) ? digitsOnly : final_text;
            }

            return "";
        }
        catch (Exception ex)
        {
            return $"Lỗi nhận diện giọng nói: {ex.Message}";
        }
    }
}
