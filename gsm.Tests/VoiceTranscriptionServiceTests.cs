using System.IO;
using gsm.Services;
using Xunit;

namespace gsm.Tests;

public class VoiceTranscriptionServiceTests
{
    [Fact]
    public void GetOrExtractScriptPath_ReturnsExistingOrCreatedScript()
    {
        string path = VoiceTranscriptionService.GetOrExtractScriptPath();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path));
        Assert.EndsWith("stt_extract.py", path);
    }

    [Fact]
    public void WorkerScripts_AutoDetectLanguageAndPreserveOtpTokenBoundaries()
    {
        string deployedScript = File.ReadAllText(
            VoiceTranscriptionService.GetOrExtractScriptPath());

        foreach (string script in new[]
                 {
                     deployedScript,
                     VoiceTranscriptionService.EmbeddedWorkerScript
                 })
        {
            Assert.Contains("detect_language(", script);
            Assert.Contains("language=detected_language or None", script);
            Assert.Contains("selected_prompt = prompts.get(detected_language)", script);
            Assert.Contains("initial_prompt=initial_prompt", script);
            Assert.Contains("def _digit_runs", script);
            Assert.Contains("candidates.count(selected) >= 2", script);
            Assert.Contains("fallback_text, fallback_info = transcribe_once(None)", script);
            Assert.Contains("--prepare-model", script);
            Assert.Contains("from faster_whisper.utils import download_model", script);
            Assert.Contains("TOOLGSM_WHISPER_CACHE_DIR", script);
            Assert.DoesNotContain("language=\"vi\"", script);
        }
    }

    [Fact]
    public async Task TranscribeAudioAsync_NonExistentFile_ReturnsError()
    {
        var result = await VoiceTranscriptionService.TranscribeAudioAsync("non_existent_file.wav");
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("Không tìm thấy file ghi âm", result.Error);
    }

    [Fact]
    public void WhisperModelCacheDirectory_IsInLocalToolGsmData()
    {
        string expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolGSM",
            "Models",
            "Whisper");

        Assert.Equal(expectedRoot, VoiceTranscriptionService.WhisperModelCacheDirectory);
    }
}
