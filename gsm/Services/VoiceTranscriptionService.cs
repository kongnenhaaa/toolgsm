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
    public bool Locked { get; set; }
    public string AudioPath { get; set; } = string.Empty;
    public string? Error { get; set; }
    public bool Success => string.IsNullOrEmpty(Error) && !string.IsNullOrEmpty(Text);
}

public static class VoiceTranscriptionService
{
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

_ANCHOR_RE = re.compile(
    r""(?:ma\s+)?(?:xac thuc|sac that|xac nhan|otp|xep|he luc)?\s*(?:cua\s*)?(?:ban|ba)?\s*(?:la\b|:|\.)\s*""
)

def _digits_of(s: str) -> str:
    tokens = re.findall(r""[a-z]+|\d+"", _norm_vn(s or """").lower())
    return """".join(t if t.isdigit() else _NORM_WORD2DIG.get(t, """") for t in tokens)

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
    if n > 6:
        return run[:6]
    return """"

def _first_otp_in(digits_str: str) -> str:
    for m in re.finditer(r""\d{4,}"", digits_str):
        o = _norm_otp(m.group(0))
        if o:
            return o
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

    p_la = norm.rfind(""la"")
    if p_la >= 0 and p_la < len(norm) - 2:
        seg_after_la = norm[p_la + 2:]
        dig_la = _digits_of(seg_after_la)
        o = _first_otp_in(dig_la)
        if o:
            return o, _digits_of(low)

    matches = list(_ANCHOR_RE.finditer(norm))
    if matches:
        seg = _digits_of(norm[matches[-1].end():matches[-1].end() + 120])
        o = _first_otp_in(seg)
        if o:
            return o, _digits_of(low)

    for kw in (""sac that"", ""xac thuc"", ""xac nhan"", ""otp""):
        start = len(norm)
        while True:
            i = norm.rfind(kw, 0, start)
            if i < 0:
                break
            seg = _digits_of(norm[i:i + 150])
            o = _first_otp_in(seg)
            if o:
                return o, _digits_of(low)
            start = i

    all_digits = _digits_of(low)
    o = _first_otp_in(all_digits)
    return o, all_digits

def main():
    if len(sys.argv) < 2:
        print(json.dumps({""error"": ""Missing audio file""}))
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
        model = WhisperModel(model_name, device=""cpu"", compute_type=""int8"")
        segments, _info = model.transcribe(
            audio,
            language=""vi"",
            beam_size=5,
            vad_filter=True
        )
        raw_text = "" "".join(s.text for s in segments).strip()
        text = clean_transcript(raw_text)
        otp, digits = extract_otp(raw_text)
        locked = is_sim_locked(raw_text)
        print(json.dumps({""text"": text, ""otp"": otp, ""digits"": digits, ""locked"": locked}, ensure_ascii=False))
    except Exception as ex:
        print(json.dumps({""error"": str(ex), ""text"": """", ""otp"": """", ""digits"": """", ""locked"": False}))

if __name__ == ""__main__"":
    main()
";

    public static string FindPythonExecutable()
    {
        string[] candidates = [
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
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }

        return "python.exe";
    }

    public static string GetOrExtractScriptPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string scriptPath = Path.Combine(baseDir, "voice_otp", "stt_extract.py");
        if (File.Exists(scriptPath))
            return scriptPath;

        string dataScriptDir = Path.Combine(AppBootstrap.DataDir, "voice_otp");
        Directory.CreateDirectory(dataScriptDir);
        string dataScriptPath = Path.Combine(dataScriptDir, "stt_extract.py");
        if (!File.Exists(dataScriptPath))
        {
            File.WriteAllText(dataScriptPath, EmbeddedScriptContent, Encoding.UTF8);
        }
        return dataScriptPath;
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

        string pythonExe = FindPythonExecutable();
        string scriptPath = GetOrExtractScriptPath();

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{scriptPath}\" \"{wavPath}\" {model}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

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
