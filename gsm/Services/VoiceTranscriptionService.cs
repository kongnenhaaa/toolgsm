using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace gsm.Services;

public class VoiceTranscriptionResult
{
    public string Text { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
    public string Digits { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public double LanguageProbability { get; set; }
    public bool Locked { get; set; }
    public string AudioPath { get; set; } = string.Empty;
    public string? Error { get; set; }
    public bool Success => string.IsNullOrEmpty(Error) && !string.IsNullOrEmpty(Text);
}

public static class VoiceTranscriptionService
{
    private static readonly SemaphoreSlim ModelDownloadLock = new(1, 1);
    public static string WhisperModelCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ToolGSM",
        "Models",
        "Whisper");

    private static readonly string EmbeddedScriptContent = @"# -*- coding: utf-8 -*-
import sys, re, json, os, unicodedata
os.environ.setdefault(""HF_HUB_DISABLE_PROGRESS_BARS"", ""1"")

WORD2DIG = {
    ""không"": ""0"", ""khong"": ""0"", ""zero"": ""0"", ""oh"": ""0"", ""kong"": ""0"",
    ""một"": ""1"", ""mot"": ""1"", ""mốt"": ""1"", ""one"": ""1"", ""mut"": ""1"",
    ""hai"": ""2"", ""two"": ""2"", ""hay"": ""2"", ""hãi"": ""2"",
    ""ba"": ""3"", ""three"": ""3"", ""bà"": ""3"", ""bá"": ""3"",
    ""bốn"": ""4"", ""bon"": ""4"", ""four"": ""4"", ""vô"": ""4"", ""vo"": ""4"", ""bộn"": ""4"", ""bộm"": ""4"", ""bom"": ""4"", ""bông"": ""4"", ""bong"": ""4"", ""bổng"": ""4"",
    ""năm"": ""5"", ""nam"": ""5"", ""lăm"": ""5"", ""five"": ""5"", ""mám"": ""5"", ""mam"": ""5"", ""lắm"": ""5"", ""mâm"": ""5"",
    ""sáu"": ""6"", ""sau"": ""6"", ""six"": ""6"", ""sáo"": ""6"",
    ""bảy"": ""7"", ""bay"": ""7"", ""seven"": ""7"", ""bài"": ""7"", ""bai"": ""7"", ""bẩy"": ""7"", ""bãi"": ""7"",
    ""tám"": ""8"", ""tam"": ""8"", ""eight"": ""8"", ""tắm"": ""8"", ""tảm"": ""8"",
    ""chín"": ""9"", ""chin"": ""9"", ""nine"": ""9"", ""chi"": ""9"", ""chính"": ""9"", ""chinh"": ""9"", ""hình"": ""9"", ""hinh"": ""9"",
}

SIM_LOCKED_KEYWORDS = [
    ""tam thoi bi khoa"", ""bi khoa"", ""sim bi khoa"",
    ""thue bao bi khoa"", ""so dien thoai bi khoa"",
    ""khoa tai khoan"", ""khoa dich vu"",
    ""locked"", ""temporarily locked"", ""account locked"",
]

def _norm_vn(s: str) -> str:
    return """".join(
        c for c in unicodedata.normalize(""NFD"", s or """")
        if unicodedata.category(c) != ""Mn""
    )

_NORM_WORD2DIG = {}
for _k, _v in WORD2DIG.items():
    _NORM_WORD2DIG.setdefault(_norm_vn(_k.lower()), _v)

def is_sim_locked(text: str) -> bool:
    norm = _norm_vn((text or """").lower())
    return any(kw in norm for kw in SIM_LOCKED_KEYWORDS)

_CODE_ANCHOR_RE = re.compile(
    r""\b(?:verification\s+code|security\s+code|authentication\s+code|""
    r""one\s*time\s+password|otp|ma\s+(?:xac\s+thuc|xac\s+nhan|""
    r""sac\s+that|sat\s+thuc|sac\s+thuc|xep))\b""
)
_REPEAT_ANCHOR_RE = re.compile(r""\b(?:i\s+repeat|repeat|xin\s+nhac\s+lai|nhac\s+lai)\b"")
_VALUE_ANCHOR_RE = re.compile(r""\b(?:is|la)\b"")
_DIGIT_TOKEN_RE = re.compile(r""[a-z]+|\d+|[^a-z\d]+"")

def _digit_runs(s: str):
    runs = []
    current = []

    def flush():
        if current:
            runs.append("""".join(current))
            current.clear()

    for token in _DIGIT_TOKEN_RE.findall(_norm_vn(s or """").lower()):
        if token.isdigit():
            current.append(token)
        elif token in _NORM_WORD2DIG:
            current.append(_NORM_WORD2DIG[token])
        elif re.fullmatch(r""[\s.,:;_\-/]+"", token):
            continue
        else:
            flush()
    flush()
    return runs

def _digits_of(s: str) -> str:
    return """".join(_digit_runs(s))

def _norm_otp(run: str) -> str:
    n = len(run)
    if n in (4, 6):
        return run
    if n >= 6 and run[:6] == run[6:12]:
        return run[:6]
    if n >= 4 and run[:4] == run[4:8]:
        return run[:4]
    if n % 6 == 0 and run == run[:6] * (n // 6):
        return run[:6]
    if n % 4 == 0 and run == run[:4] * (n // 4):
        return run[:4]
    return """"

def _otp_candidates(text: str):
    candidates = []
    for run in _digit_runs(text):
        o = _norm_otp(run)
        if o:
            candidates.append(o)
    return candidates

def _candidate_after_matches(norm: str, pattern, window: int = 180):
    for match in reversed(list(pattern.finditer(norm))):
        candidates = _otp_candidates(norm[match.end():match.end() + window])
        if candidates:
            return candidates[0]
    return """"

def clean_transcript(text: str) -> str:
    if not text:
        return text
    rules = [
        (r'(?i)\b(m[ảạáàa]\s+(?:x[áa]c|ph[áa]t)\s+(?:tr[ụu]c|th[ựu]c|nh[ậa]n|th[ậa]t)\s+c[ủu]a\s+b[ạa][tnc](?:\s+l[àa])?)\b', 'Mã xác thực của bạn là'),
        (r'(?i)\b(m[ạa]c\s+s[ắáa][tc]\s+(?:k[ìi]|c[ửu])\s+c[ủu]a\s+b[ạa]n\s+l[àa])\b', 'Mã xác thực của bạn là'),
        (r'(?i)\bm[áa]u\s+[sx][ếêe]p\s+[^,\.0-9]+[,\.]?\s*', 'Mã xác thực của bạn là: '),
        (r'(?i)\b(xin\s+(?:[nl]|ng)[ấâắa][tc]\s+l[ạa]i)\b', 'Xin nhắc lại'),
        (r'(?i)\b(xin\s+c[ảa]m\s+[ơo]n)\b', 'Xin cảm ơn'),
    ]
    res = text
    for pat, repl in rules:
        res = re.sub(pat, repl, res)
    return re.sub(r'\s+', ' ', res).strip()

def extract_otp(text: str):
    low = (text or """").lower()
    norm = _norm_vn(low)

    if is_sim_locked(text):
        return """", _digits_of(low)

    all_digits = _digits_of(low)
    for pattern in (_CODE_ANCHOR_RE, _REPEAT_ANCHOR_RE, _VALUE_ANCHOR_RE):
        o = _candidate_after_matches(norm, pattern)
        if o:
            return o, all_digits

    candidates = _otp_candidates(norm)
    if not candidates:
        return """", all_digits

    winner = max(
        range(len(candidates)),
        key=lambda index: (candidates.count(candidates[index]), index)
    )
    selected = candidates[winner]
    if candidates.count(selected) >= 2:
        return selected, all_digits

    ordinary_words = [
        token for token in re.findall(r""[a-z]+"", norm)
        if token not in _NORM_WORD2DIG
    ]
    return (selected if not ordinary_words else """"), all_digits

def main():
    if len(sys.argv) < 2:
        print(json.dumps({""error"": ""Missing audio file""}))
        return
    if sys.argv[1] == ""--prepare-model"":
        model_name = sys.argv[2] if len(sys.argv) > 2 else ""small""
        cache_dir = sys.argv[3] if len(sys.argv) > 3 else None
        try:
            from faster_whisper.utils import download_model
            model_path = download_model(model_name, cache_dir=cache_dir)
            print(json.dumps({""ready"": True, ""model_path"": model_path}))
        except Exception as ex:
            print(json.dumps({""error"": str(ex), ""ready"": False}))
        return
    audio = sys.argv[1]
    model_name = sys.argv[2] if len(sys.argv) > 2 else ""small""
    try:
        sys.stdout.reconfigure(encoding=""utf-8"", errors=""replace"")
        sys.stderr.reconfigure(encoding=""utf-8"", errors=""replace"")
    except Exception:
        pass
    if not os.path.exists(audio):
        print(json.dumps({""error"": f""Audio not found: {audio}""}))
        return
    try:
        from faster_whisper import WhisperModel
        from faster_whisper.audio import decode_audio
        model = WhisperModel(
            model_name,
            device=""cpu"",
            compute_type=""int8"",
            download_root=os.environ.get(""TOOLGSM_WHISPER_CACHE_DIR"") or None,
            local_files_only=os.path.isdir(model_name),
        )
        audio_data = decode_audio(audio)
        detected_language, language_probability, _all_languages = model.detect_language(
            audio=audio_data,
            vad_filter=True,
            language_detection_segments=3
        )
        prompts = {
            ""en"": ""Your verification code is zero one two three four five six seven eight nine. I repeat, your verification code is."",
            ""vi"": ""Mã xác thực của bạn là không một hai ba bốn năm sáu bảy tám chín. Xin nhắc lại mã xác thực.""
        }
        selected_prompt = prompts.get(detected_language)

        def transcribe_once(initial_prompt):
            segments, transcription_info = model.transcribe(
                audio_data,
                language=detected_language or None,
                initial_prompt=initial_prompt,
                beam_size=5,
                vad_filter=True
            )
            return "" "".join(s.text for s in segments).strip(), transcription_info

        raw_text, info = transcribe_once(selected_prompt)
        otp, digits = extract_otp(raw_text)
        normalized_text = _norm_vn(raw_text.lower())
        has_otp_context = (
            _CODE_ANCHOR_RE.search(normalized_text)
            or _REPEAT_ANCHOR_RE.search(normalized_text)
        )
        if selected_prompt and not otp and has_otp_context:
            fallback_text, fallback_info = transcribe_once(None)
            fallback_otp, fallback_digits = extract_otp(fallback_text)
            if fallback_otp:
                raw_text = fallback_text
                info = fallback_info
                otp = fallback_otp
                digits = fallback_digits

        text = clean_transcript(raw_text)
        locked = is_sim_locked(raw_text)
        print(json.dumps({
            ""text"": text,
            ""otp"": otp,
            ""digits"": digits,
            ""locked"": locked,
            ""language"": info.language or detected_language,
            ""language_probability"": language_probability
        }, ensure_ascii=False))
    except Exception as ex:
        print(json.dumps({""error"": str(ex), ""text"": """", ""otp"": """", ""digits"": """", ""locked"": False}))

if __name__ == ""__main__"":
    main()
";

    internal static string EmbeddedWorkerScript => EmbeddedScriptContent;

    public static string FindPythonExecutable()
    {
        string[] candidates = [
            Path.Combine(AppContext.BaseDirectory, "python_runtime", "python.exe"),
            "python.exe",
            "python",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\python.exe"),
            @"C:\Python312\python.exe",
            @"C:\Python311\python.exe",
            @"C:\Python310\python.exe"
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                if (Path.IsPathRooted(candidate) && File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }

        // Let Windows resolve python.exe from PATH only after the bundled
        // runtime and known absolute installations have been checked.
        return "python.exe";
    }

    public static string GetOrExtractScriptPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(baseDir, "voice_otp", "stt_extract.py");
    }

    public static Task<string?> EnsureDefaultModelAvailableAsync(
        CancellationToken ct = default) =>
        EnsureModelAvailableAsync("small", ct);

    private static async Task<string?> EnsureModelAvailableAsync(
        string model,
        CancellationToken ct)
    {
        string bundledModel = Path.Combine(
            AppContext.BaseDirectory,
            "voice_otp",
            "models",
            "faster-whisper-" + model);
        if (Directory.Exists(bundledModel) || IsModelCached()) return null;

        await ModelDownloadLock.WaitAsync(ct);
        try
        {
            if (Directory.Exists(bundledModel) || IsModelCached()) return null;
            Directory.CreateDirectory(WhisperModelCacheDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = FindPythonExecutable(),
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ConfigureVoicePythonEnvironment(startInfo);
            startInfo.ArgumentList.Add(GetOrExtractScriptPath());
            startInfo.ArgumentList.Add("--prepare-model");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add(WhisperModelCacheDirectory);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return "Không khởi động được tiến trình tải model giọng nói.";
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            if (process.ExitCode == 0 && IsModelCached()) return null;

            string detail = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Reverse()
                .FirstOrDefault() ?? stderr.Trim();
            return string.IsNullOrWhiteSpace(detail)
                ? $"Tải model giọng nói thất bại (mã {process.ExitCode})."
                : $"Tải model giọng nói thất bại: {detail}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Tải model giọng nói thất bại: {ex.Message}";
        }
        finally
        {
            ModelDownloadLock.Release();
        }
    }

    private static bool IsModelCached()
    {
        try
        {
            return Directory.Exists(WhisperModelCacheDirectory)
                && Directory.EnumerateFiles(
                        WhisperModelCacheDirectory,
                        "model.bin",
                        SearchOption.AllDirectories)
                    .Any(path => new FileInfo(path).Length > 400_000_000);
        }
        catch
        {
            return false;
        }
    }

    private static void ConfigureVoicePythonEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        startInfo.Environment["DO_NOT_TRACK"] = "1";
        startInfo.Environment["TOOLGSM_WHISPER_CACHE_DIR"] =
            WhisperModelCacheDirectory;
    }

    public static async Task<VoiceTranscriptionResult> TranscribeAudioAsync(
        string wavPath,
        string model = "small",
        CancellationToken ct = default)
    {
        var result = new VoiceTranscriptionResult { AudioPath = wavPath };

        if (!File.Exists(wavPath))
        {
            result.Error = $"Không tìm thấy file ghi âm: {wavPath}";
            return result;
        }

        string modelArgument;
        if (Path.IsPathRooted(model))
        {
            if (!Directory.Exists(model))
            {
                result.Error = "Không tìm thấy model nhận dạng giọng nói đã chọn.";
                return result;
            }
            modelArgument = model;
        }
        else
        {
            string bundledModel = Path.Combine(
                AppContext.BaseDirectory,
                "voice_otp",
                "models",
                "faster-whisper-" + model);
            if (Directory.Exists(bundledModel))
            {
                modelArgument = bundledModel;
            }
            else
            {
                string? modelError = await EnsureModelAvailableAsync(model, ct);
                if (modelError != null)
                {
                    result.Error = modelError;
                    return result;
                }
                modelArgument = model;
            }
        }

        string pythonExe = FindPythonExecutable();
        string scriptPath = GetOrExtractScriptPath();

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        ConfigureVoicePythonEnvironment(psi);
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add(modelArgument);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                result.Error = string.IsNullOrWhiteSpace(stderr) 
                    ? $"Process exited with code {process.ExitCode}" 
                    : stderr.Trim();
                return result;
            }

            string? jsonLine = null;
            using (var reader = new StringReader(stdout))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("{") && line.EndsWith("}"))
                    {
                        jsonLine = line;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(jsonLine))
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var errProp) && !string.IsNullOrWhiteSpace(errProp.GetString()))
                {
                    result.Error = errProp.GetString();
                }
                if (root.TryGetProperty("text", out var textProp))
                    result.Text = textProp.GetString() ?? string.Empty;
                if (root.TryGetProperty("otp", out var otpProp))
                    result.Otp = otpProp.GetString() ?? string.Empty;
                if (root.TryGetProperty("digits", out var digProp))
                    result.Digits = digProp.GetString() ?? string.Empty;
                if (root.TryGetProperty("language", out var languageProp))
                    result.Language = languageProp.GetString() ?? string.Empty;
                if (root.TryGetProperty("language_probability", out var probabilityProp)
                    && probabilityProp.TryGetDouble(out double probability))
                {
                    result.LanguageProbability = probability;
                }
                if (root.TryGetProperty("locked", out var lockProp))
                    result.Locked = lockProp.GetBoolean();
            }
            else
            {
                result.Text = stdout.Trim();
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Tiến trình dịch âm thanh đã bị hủy.";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"Lỗi xử lý Voice STT: {ex.Message}";
            return result;
        }
    }
}
