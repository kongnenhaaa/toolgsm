# -*- coding: utf-8 -*-
"""STT worker: file ghi am cuoc goi -> faster-whisper -> OTP.
Usage: python stt_extract.py <audio_file> [model=small]
In ra stdout 1 dong JSON: {"text": ..., "otp": ..., "digits": ..., "locked": ...}"""
import sys, re, json, os, unicodedata

os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")

WORD2DIG = {
    "không": "0", "khong": "0", "zero": "0", "oh": "0", "kong": "0",
    "một": "1", "mot": "1", "mốt": "1", "one": "1", "mut": "1",
    "hai": "2", "two": "2", "hay": "2", "hãi": "2",
    "ba": "3", "three": "3", "bà": "3", "bá": "3",
    "bốn": "4", "bon": "4", "four": "4", "vô": "4", "vo": "4", "bộn": "4", "bộm": "4", "bom": "4", "bông": "4", "bong": "4", "bổng": "4",
    "năm": "5", "nam": "5", "lăm": "5", "five": "5", "mám": "5", "mam": "5", "lắm": "5", "mâm": "5",
    "sáu": "6", "sau": "6", "six": "6", "sáo": "6",
    "bảy": "7", "bay": "7", "seven": "7", "bài": "7", "bai": "7", "bẩy": "7", "bãi": "7",
    "tám": "8", "tam": "8", "eight": "8", "tắm": "8", "tảm": "8",
    "chín": "9", "chin": "9", "nine": "9", "chi": "9", "chính": "9", "chinh": "9", "hình": "9", "hinh": "9",
}

SIM_LOCKED_KEYWORDS = [
    "tam thoi bi khoa", "bi khoa", "sim bi khoa",
    "thue bao bi khoa", "so dien thoai bi khoa",
    "khoa tai khoan", "khoa dich vu",
    "locked", "temporarily locked", "account locked",
]


def _norm_vn(s: str) -> str:
    return "".join(
        c for c in unicodedata.normalize("NFD", s or "")
        if unicodedata.category(c) != "Mn"
    )


_NORM_WORD2DIG = {}
for _k, _v in WORD2DIG.items():
    _NORM_WORD2DIG.setdefault(_norm_vn(_k.lower()), _v)


def is_sim_locked(text: str) -> bool:
    norm = _norm_vn((text or "").lower())
    return any(kw in norm for kw in SIM_LOCKED_KEYWORDS)


_CODE_ANCHOR_RE = re.compile(
    r"\b(?:verification\s+code|security\s+code|authentication\s+code|"
    r"one\s*time\s+password|otp|ma\s+(?:xac\s+thuc|xac\s+nhan|"
    r"sac\s+that|sat\s+thuc|sac\s+thuc|xep))\b"
)
_REPEAT_ANCHOR_RE = re.compile(r"\b(?:i\s+repeat|repeat|xin\s+nhac\s+lai|nhac\s+lai)\b")
_VALUE_ANCHOR_RE = re.compile(r"\b(?:is|la)\b")
_DIGIT_TOKEN_RE = re.compile(r"[a-z]+|\d+|[^a-z\d]+")


def _digit_runs(s: str):
    """Join adjacent spoken digits without crossing ordinary words.

    This keeps modem-style output such as "6.38205" intact, but prevents
    "six digit verification code is 638205" from becoming 6638205.
    """
    runs = []
    current = []

    def flush():
        if current:
            runs.append("".join(current))
            current.clear()

    for token in _DIGIT_TOKEN_RE.findall(_norm_vn(s or "").lower()):
        if token.isdigit():
            current.append(token)
        elif token in _NORM_WORD2DIG:
            current.append(_NORM_WORD2DIG[token])
        elif re.fullmatch(r"[\s.,:;_\-/]+", token):
            continue
        else:
            flush()
    flush()
    return runs


def _digits_of(s: str) -> str:
    return "".join(_digit_runs(s))


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
    return ""


def _otp_candidates(text: str):
    candidates = []
    for run in _digit_runs(text):
        o = _norm_otp(run)
        if o:
            candidates.append(o)
    return candidates


def _candidate_after_matches(norm: str, pattern, window: int = 180):
    # Automated calls normally repeat the OTP, so inspect the last matching
    # context first. It is less likely to contain a noisy preamble.
    for match in reversed(list(pattern.finditer(norm))):
        candidates = _otp_candidates(norm[match.end():match.end() + window])
        if candidates:
            return candidates[0]
    return ""


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
    low = (text or "").lower()
    norm = _norm_vn(low)

    if is_sim_locked(text):
        return "", _digits_of(low)

    all_digits = _digits_of(low)
    for pattern in (_CODE_ANCHOR_RE, _REPEAT_ANCHOR_RE, _VALUE_ANCHOR_RE):
        o = _candidate_after_matches(norm, pattern)
        if o:
            return o, all_digits

    candidates = _otp_candidates(norm)
    if not candidates:
        return "", all_digits

    # Prefer a value spoken more than once. This accepts calls that only say
    # "please use ... again ..." without an OTP keyword.
    winner = max(
        range(len(candidates)),
        key=lambda index: (candidates.count(candidates[index]), index)
    )
    selected = candidates[winner]
    if candidates.count(selected) >= 2:
        return selected, all_digits

    # A bare spoken/numeric code is also valid. Do not promote an isolated year,
    # phone number or account number from a longer ordinary transcript to OTP.
    ordinary_words = [
        token for token in re.findall(r"[a-z]+", norm)
        if token not in _NORM_WORD2DIG
    ]
    return (selected if not ordinary_words else ""), all_digits


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "Missing audio file argument"}))
        return
    if sys.argv[1] == "--prepare-model":
        model_name = sys.argv[2] if len(sys.argv) > 2 else "small"
        cache_dir = sys.argv[3] if len(sys.argv) > 3 else None
        try:
            from faster_whisper.utils import download_model
            model_path = download_model(model_name, cache_dir=cache_dir)
            print(json.dumps({"ready": True, "model_path": model_path}))
        except Exception as ex:
            print(json.dumps({"error": str(ex), "ready": False}))
        return
    audio = sys.argv[1]
    model_name = sys.argv[2] if len(sys.argv) > 2 else "small"
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    
    if not os.path.exists(audio):
        print(json.dumps({"error": f"Audio file not found: {audio}"}))
        return

    try:
        from faster_whisper import WhisperModel
        from faster_whisper.audio import decode_audio
        model = WhisperModel(
            model_name,
            device="cpu",
            compute_type="int8",
            download_root=os.environ.get("TOOLGSM_WHISPER_CACHE_DIR") or None,
            local_files_only=os.path.isdir(model_name),
        )
        audio_data = decode_audio(audio)
        detected_language, language_probability, _all_languages = model.detect_language(
            audio=audio_data,
            vad_filter=True,
            language_detection_segments=3
        )
        prompts = {
            "en": "Your verification code is zero one two three four five six seven eight nine. I repeat, your verification code is.",
            "vi": "Mã xác thực của bạn là không một hai ba bốn năm sáu bảy tám chín. Xin nhắc lại mã xác thực."
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
            return " ".join(s.text for s in segments).strip(), transcription_info

        raw_text, info = transcribe_once(selected_prompt)
        otp, digits = extract_otp(raw_text)

        # A language-specific prompt improves long Vietnamese number sequences,
        # but can occasionally hurt a very short code. Retry without the prompt
        # only when the transcript clearly describes an OTP and no valid 4/6
        # digit value was found. Ordinary long calls still use a single pass.
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
            "text": text,
            "otp": otp,
            "digits": digits,
            "locked": locked,
            "language": info.language or detected_language,
            "language_probability": language_probability
        }, ensure_ascii=False))
    except Exception as ex:
        print(json.dumps({"error": str(ex), "text": "", "otp": "", "digits": "", "locked": False}))


if __name__ == "__main__":
    main()
