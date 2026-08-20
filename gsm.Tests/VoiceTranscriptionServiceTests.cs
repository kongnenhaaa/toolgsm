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
    public async Task TranscribeAudioAsync_NonExistentFile_ReturnsError()
    {
        var result = await VoiceTranscriptionService.TranscribeAudioAsync("non_existent_file.wav");
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("Không tìm thấy file ghi âm", result.Error);
    }
}
