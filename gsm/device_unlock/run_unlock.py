# -*- coding: utf-8 -*-
"""ToolGSM bridge for the existing ekyc_full.py DKTTTB workflow.

The phone and OTP are read from stdin so the OTP is never exposed in the
process command line.  The original module remains the single implementation
of the eKYC/DKTTTB API flow.
"""

import importlib.util
import hashlib
import json
import os
import random
import sys


for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


RESULT_PREFIX = "TOOLGSM_RESULT_JSON:"
OTP_RESULT_PREFIX = "TOOLGSM_OTP_REQUEST_JSON:"


def read_request():
    raw = sys.stdin.readline().lstrip("\ufeff")
    if not raw.strip():
        raise ValueError("ToolGSM không gửi dữ liệu DKTTTB cho Python")
    return json.loads(raw)


def emit_result(**value):
    print(RESULT_PREFIX + json.dumps(value, ensure_ascii=False), flush=True)


def emit_otp_result(**value):
    print(OTP_RESULT_PREFIX + json.dumps(value, ensure_ascii=False), flush=True)


def _compact_error_text(value):
    text = " ".join(str(value or "").strip().split())
    if not text:
        return ""
    # Preserve the system reason while redacting credential-like assignments.
    import re
    text = re.sub(
        r"(?i)\b(authorization|bearer|token|session|secret|signature|requestdata)"
        r"\s*[:=]\s*[^\s,|}]+",
        lambda match: match.group(1) + "=[REDACTED]",
        text,
    )
    return text[:600]


def extract_ekyc_failure_detail(line):
    """Return (priority, safe detail) from one eKYC child output line."""
    text = str(line or "").strip()
    if not text:
        return 0, ""

    json_start = text.find("{")
    if json_start >= 0:
        raw = text[json_start:].strip()
        try:
            payload = json.loads(raw)
        except Exception:
            payload = None
        if isinstance(payload, dict):
            code = str(
                payload.get(
                    "errorCode",
                    payload.get("error_code", payload.get("code", "")),
                )
                or ""
            ).strip()
            message = str(
                payload.get(
                    "message",
                    payload.get(
                        "errorMessage",
                        payload.get("error", payload.get("description", "")),
                    ),
                )
                or ""
            ).strip()
            if code.lower() not in ("", "0", "00", "200", "success", "true"):
                detail = f"errorCode={code}"
                if message:
                    detail += f" | {message}"
                return 100, _compact_error_text(detail)

    if "FAILED:" in text:
        return 100, _compact_error_text(text.split("FAILED:", 1)[1])
    if "SERVER ERROR RESPONSE:" in text:
        return 95, _compact_error_text(
            text.split("SERVER ERROR RESPONSE:", 1)[1]
        )

    lowered = text.lower()
    import re
    squashed = re.sub(r"\s+", "", lowered)
    if any(marker in lowered for marker in (
        "verifyimeichange error:",
        "verifyimeichange failed",
        "getchallengecodesdkekyc timeout/error:",
        "savelogekyc error:",
        "exception:",
    )):
        return 90, _compact_error_text(text)
    if "skipped:livenessfailed" in squashed:
        return 60, "eKYC liveness thất bại nên hệ thống không gọi verifyImeiChange"
    if any(marker in squashed for marker in (
        "liveness:failed",
        "savelogekyc:failed",
        "verifyimeichange:failed",
    )):
        return 40, _compact_error_text(text)
    if re.search(r"\bhttp\s*[:=]?\s*[45]\d\d\b", lowered):
        return 35, _compact_error_text(text)
    if any(marker in lowered for marker in (
        " error", "error:", "exception", "traceback", "timeout",
        "invalid", "denied", "từ chối", "thất bại", "that bai",
        "không thành công", "khong thanh cong", "không hợp lệ",
        "khong hop le", "không gọi", "khong goi",
    )):
        return 30, _compact_error_text(text)
    return 0, ""


def fallback_ekyc_output_detail(line):
    """Keep a safe last output line for unknown failure formats."""
    detail = _compact_error_text(line)
    if not detail or detail.startswith((RESULT_PREFIX, OTP_RESULT_PREFIX)):
        return ""
    if not any(character.isalnum() for character in detail):
        return ""
    return detail


def stable_device_profile(device_key):
    """Build one reproducible VNPT Android profile for a COM port."""
    normalized_key = str(device_key or "").strip().upper()
    if not normalized_key:
        raise ValueError("Thiếu tên COM để tạo fingerprint thiết bị ổn định")

    seed_material = hashlib.sha256(
        ("ToolGSM.MyVNPT.DeviceUnlock.v1:" + normalized_key).encode("utf-8")
    ).digest()
    generator = random.Random(int.from_bytes(seed_material, "big"))
    models = [
        ("V2057", "11"),
        ("CPH2179", "11"),
        ("V2206", "12"),
        ("SM-A037F", "12"),
        ("M2103K19G", "11"),
        ("RMX3430", "12"),
        ("SM-A135F", "12"),
        ("CPH2269", "12"),
        ("SM-A225F", "11"),
        ("RMX3195", "11"),
        ("SM-A536B", "13"),
        ("V2218", "12"),
        ("CPH2325", "12"),
        ("M2012K11AG", "12"),
    ]
    model, android_ver = generator.choice(models)
    # Keep this UUID derivation in sync with MyVnptService.CreateStableDeviceInfo
    # so the same COM has the same VNPT device ID in both ToolGSM tabs.
    identity_hex = hashlib.sha256(
        ("ToolGSM.MyVNPT.Device.v1:" + normalized_key).encode("utf-8")
    ).hexdigest()
    dev_uuid = (
        f"{identity_hex[:8]}-{identity_hex[8:12]}-{identity_hex[12:16]}-"
        f"{identity_hex[16:20]}-{identity_hex[20:32]}"
    )
    fcm_chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_:"
    fcm_token = "".join(generator.choices(fcm_chars, k=168))
    mac = "".join(generator.choices("0123456789abcdef", k=16))
    di = (
        f"{dev_uuid}|{dev_uuid}|unknown|Android||3.3.99.Prd|"
        f"{model}|{android_ver}|"
    )
    return {
        "uuid": dev_uuid,
        "model": model,
        "android_ver": android_ver,
        "fcm_token": fcm_token,
        "mac": mac,
        "di": di,
    }


def request_login_otp(module, phone, device_key):
    """Use OTP login with a stable fingerprint assigned to this COM port."""
    profile = stable_device_profile(device_key)
    required = ("di", "model", "android_ver", "fcm_token", "mac")
    if any(not str(profile.get(field) or "").strip() for field in required):
        raise RuntimeError("eKYC không tạo đủ thông tin thiết bị")

    session = module.requests.Session()
    session.mount("https://", module.DhAdapter())
    response = session.post(
        "https://api-myvnpt.vnpt.vn/mapi_v2/services/otp_send",
        headers={
            "Authorization": (
                "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b"
            ),
            "Content-Type": "application/json; charset=UTF-8",
            "Device-Info": profile["di"],
            "Language": "vi_VN",
            "User-Agent": "okhttp/4.7.2",
        },
        json={"msisdn": phone, "otp_service": "authen_msisdn"},
        timeout=60,
        verify=False,
    )
    try:
        body = response.json()
    except Exception:
        body = {}
    error_code = str(body.get("error_code", body.get("errorCode", "?")))
    message = str(body.get("message") or "")
    success = response.status_code == 200 and error_code == "0"
    emit_otp_result(
        success=success,
        phone=phone,
        httpStatus=int(response.status_code),
        errorCode=error_code,
        message=message,
        deviceProfile={field: str(profile[field]) for field in required}
        if success else None,
        userAgent="okhttp/4.7.2",
    )
    return 0 if success else 1


def patch_otp_login_payload(module):
    """Translate the eKYC helper's OTP field to pass_myvnpt's password field."""
    request_module = module.requests
    original_session_factory = request_module.Session

    class OtpLoginSession:
        def __init__(self, *args, **kwargs):
            self._inner = original_session_factory(*args, **kwargs)

        def post(self, url, *args, **kwargs):
            body = kwargs.get("json")
            if (str(url).rstrip("/").endswith("/authen_msisdn")
                    and isinstance(body, dict)
                    and body.get("mode") == "otp"
                    and "otp" in body):
                body = dict(body)
                body["password"] = body.pop("otp")
                kwargs["json"] = body
            return self._inner.post(url, *args, **kwargs)

        def __getattr__(self, name):
            return getattr(self._inner, name)

    request_module.Session = OtpLoginSession


def main():
    if len(sys.argv) != 2:
        emit_result(success=False, message="Thiếu đường dẫn ekyc_full.py")
        return 2

    source_path = os.path.abspath(sys.argv[1])
    if not os.path.isfile(source_path):
        emit_result(success=False, message=f"Không tìm thấy script: {source_path}")
        return 2

    try:
        request = read_request()
    except Exception as exc:
        emit_result(success=False, message=f"Dữ liệu đầu vào không hợp lệ: {exc}")
        return 2

    action = str(request.get("action") or "run").strip()
    phone = str(request.get("phone") or "").strip()
    if not phone:
        emit_result(success=False, message="Số điện thoại không hợp lệ")
        return 2

    source_dir = os.path.dirname(source_path)
    if source_dir not in sys.path:
        sys.path.insert(0, source_dir)

    try:
        spec = importlib.util.spec_from_file_location(
            "toolgsm_ekyc_full", source_path)
        if spec is None or spec.loader is None:
            raise RuntimeError("Không nạp được module DKTTTB")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        if action == "requestOtp":
            return request_login_otp(
                module,
                phone,
                str(request.get("deviceKey") or ""),
            )

        otp = str(request.get("otp") or "").strip()
        device_profile = request.get("deviceProfile") or {}
        if not otp.isdigit():
            emit_result(success=False, message="OTP đăng nhập không hợp lệ")
            return 2
        required_profile_fields = (
            "di", "model", "android_ver", "fcm_token", "mac")
        if not isinstance(device_profile, dict) or any(
                not str(device_profile.get(field) or "").strip()
                for field in required_profile_fields):
            emit_result(
                success=False,
                message="Thiếu thông tin thiết bị của phiên OTP")
            return 2
        device_profile = {
            field: str(device_profile[field]).strip()
            for field in required_profile_fields
        }

        # otp_send and mode=otp must use the exact same fingerprint. Each
        # ToolGSM worker is a separate Python process, so profiles stay fully
        # isolated even when many ports run concurrently.
        module._random_device_profile = lambda: dict(device_profile)
        patch_otp_login_payload(module)

        already_completed = False
        best_failure_priority = 0
        best_failure_detail = ""
        last_safe_output = ""

        def forward_log(message):
            nonlocal already_completed, best_failure_priority, best_failure_detail
            nonlocal last_safe_output
            text = str(message or "")
            if "DKTTTB DA HOAN THANH TRUOC DO" in text:
                already_completed = True
            priority, detail = extract_ekyc_failure_detail(text)
            if detail and priority >= best_failure_priority:
                best_failure_priority = priority
                best_failure_detail = detail
            fallback = fallback_ekyc_output_detail(text)
            if fallback:
                last_safe_output = fallback
            print(text, flush=True)

        success, full_name, msisdn, error = module.run_ekyc_auto(
            phone,
            password="",
            otp=otp,
            use_local_img=True,
            log_cb=forward_log,
            tag="TOOLGSM",
        )
        if success and already_completed:
            message = "Đã hoàn thành trước đó"
        elif success:
            message = "Mở khóa đổi thiết bị thành công"
        else:
            returned_error = str(error or "").strip()
            message = (
                best_failure_detail
                or returned_error
                or (
                    f"eKYC không trả mã lỗi; kết quả cuối: {last_safe_output}"
                    if last_safe_output
                    else ""
                )
                or "DKTTTB thất bại nhưng tiến trình eKYC không trả lý do"
            )
        emit_result(
            success=bool(success),
            alreadyCompleted=already_completed,
            fullName=str(full_name or ""),
            msisdn=str(msisdn or phone),
            message=message,
        )
        return 0 if success else 1
    except Exception as exc:
        if str(request.get("action") or "run").strip() == "requestOtp":
            emit_otp_result(
                success=False,
                phone=phone,
                httpStatus=0,
                errorCode="exception",
                message=f"Lỗi yêu cầu OTP eKYC: {exc}",
                deviceProfile=None,
            )
        else:
            emit_result(success=False, message=f"Lỗi chạy DKTTTB: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
