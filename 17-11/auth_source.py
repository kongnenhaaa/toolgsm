# -*- coding: utf-8 -*-
"""Authentication bridge for the image sources used by ekyc_full.py.

Credentials are accepted through stdin and never placed on the process command
line.  Successful logins are persisted by the existing eKYC module using its
own accounts.json and history/onebss_config.json formats.
"""

import importlib.util
import base64
import json
import os
import sys
import time


RESULT_PREFIX = "TOOLGSM_AUTH_RESULT_JSON:"


def configure_utf8_stdio():
    """Keep Vietnamese API messages safe on Windows code pages."""
    os.environ["PYTHONIOENCODING"] = "utf-8"
    os.environ["PYTHONUTF8"] = "1"
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (OSError, ValueError):
                pass


configure_utf8_stdio()


def read_request():
    # .NET/Windows may prefix the first redirected stdin line with U+FEFF.
    # JSON itself does not accept that marker, so remove only leading BOMs.
    raw = sys.stdin.readline().lstrip("\ufeff")
    if not raw.strip():
        raise ValueError("ToolGSM không gửi dữ liệu đăng nhập cho Python")
    return json.loads(raw)


def emit_result(**value):
    # ASCII JSON escapes are decoded back to Vietnamese by System.Text.Json,
    # and remain safe even if a parent process changes the Windows code page.
    line = RESULT_PREFIX + json.dumps(value, ensure_ascii=True) + "\n"
    buffer = getattr(sys.stdout, "buffer", None)
    if buffer is not None:
        buffer.write(line.encode("utf-8", errors="replace"))
        buffer.flush()
    else:
        print(line, end="", flush=True)


def read_json_response(response, service_name):
    """Return JSON or a user-actionable error when a gateway/VPN returns HTML."""
    try:
        return response.json()
    except ValueError as exc:
        content_type = str(response.headers.get("Content-Type") or "không xác định")
        raise RuntimeError(
            f"{service_name} không trả dữ liệu hợp lệ (HTTP {response.status_code}, "
            f"Content-Type: {content_type}). Kiểm tra mạng, VPN/proxy hoặc máy chủ rồi thử lại."
        ) from exc


def decode_access_token_payload(token):
    """Decode public JWT claims without treating them as server verification."""
    try:
        parts = str(token or "").split(".")
        if len(parts) != 3 or not all(parts):
            return {}
        encoded = parts[1] + "=" * (-len(parts[1]) % 4)
        value = json.loads(base64.urlsafe_b64decode(encoded).decode("utf-8"))
        return value if isinstance(value, dict) else {}
    except (ValueError, TypeError, UnicodeDecodeError, json.JSONDecodeError):
        return {}


def plausible_signed_access_token(token):
    """Allow profile-endpoint fallback only for signed-looking live JWTs.

    OneBSS still verifies the signature on the first business API call.
    """
    try:
        parts = str(token or "").split(".")
        if len(parts) != 3 or not all(parts):
            return False
        encoded_header = parts[0] + "=" * (-len(parts[0]) % 4)
        header = json.loads(base64.urlsafe_b64decode(encoded_header).decode("utf-8"))
        algorithm = str(header.get("alg") or "").strip().lower()
        claims = decode_access_token_payload(token)
        expires_at = float(claims.get("exp"))
        username = str(claims.get("user_name") or claims.get("sub") or "").strip()
        return bool(algorithm and algorithm != "none" and expires_at > time.time() and username)
    except (ValueError, TypeError, UnicodeDecodeError, json.JSONDecodeError):
        return False


def token_identity(token):
    claims = decode_access_token_payload(token)
    username = str(claims.get("user_name") or claims.get("sub") or "").strip()
    device_id = ""
    for key in ("id_thietbi", "device_id", "deviceId"):
        value = claims.get(key)
        if value is not None and not isinstance(value, (dict, list, tuple, set)):
            device_id = str(value).strip()
            if device_id:
                break
    return username, device_id


def load_source(source_path):
    source_dir = os.path.dirname(source_path)
    if source_dir not in sys.path:
        sys.path.insert(0, source_dir)
    spec = importlib.util.spec_from_file_location(
        "toolgsm_ekyc_auth_source", source_path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Không nạp được module DKTTTB")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def auth_status(module):
    cbss_accounts, cbss_last_user = module._CBSS_RUNTIME.saved_credentials()
    employ_config = module._load_saved_config()
    employ_accounts = employ_config.get("accounts") or []
    employ_last_user = str(employ_config.get("last_user") or "")
    if not employ_last_user and employ_accounts:
        employ_last_user = str(employ_accounts[0].get("u") or "")
    return {
        "cbssConfigured": bool(
            module._CBSS_RUNTIME.logged_in
            or module._CBSS_RUNTIME.has_saved_session()
        ),
        "employConfigured": bool(employ_config.get("token")),
        "cbssUsername": str(cbss_last_user or ""),
        "employUsername": employ_last_user,
    }


def ping_onebss_session(module):
    config = module._load_saved_config()
    token = str(config.get("token") or "")
    device_id = str(config.get("device_id") or module.ONEBSS_DEVICE_ID)
    if not token:
        return {
            "valid": False,
            "http_status": 0,
            "message": "Chưa lưu token OneBSS",
        }

    response = None
    try:
        response = module.requests.post(
            f"{module.ONEBSS_BASE}/quantri/user/thongtin_nv",
            headers=module._onebss_headers(token, device_id),
            json={},
            timeout=30,
            verify=False,
        )
        status = int(response.status_code or 0)
        try:
            payload = response.json()
        except Exception:
            payload = {}
        code = str(
            payload.get("error_code", payload.get("errorCode", ""))
            if isinstance(payload, dict)
            else ""
        )
        server_message = str(
            payload.get("message", "") if isinstance(payload, dict) else ""
        ).strip()
        auth_text = f"{code} {server_message}".lower()
        token_rejected = (
            status in (401, 403)
            or any(marker in auth_text for marker in (
                "token không hợp lệ",
                "token khong hop le",
                "token hết hạn",
                "token het han",
                "unauthorized",
                "expired",
                "bss-00000401",
                "bss-00000403",
            ))
        )
        jwt_fallback = not token_rejected and plausible_signed_access_token(token)
        valid = (status == 200 and not token_rejected) or jwt_fallback
        if valid:
            message = (
                "HTTP 200 - phiên OneBSS còn hiệu lực"
                if status == 200
                else "JWT còn hạn; OneBSS sẽ xác nhận bằng API nghiệp vụ"
            )
        elif token_rejected:
            message = f"HTTP {status} - phiên OneBSS đã hết hạn"
        else:
            message = f"HTTP {status} - OneBSS không trả trạng thái phiên hợp lệ"
        if code:
            message += f" ({code})"
        return {
            "valid": valid,
            "http_status": status,
            "message": message,
        }
    except Exception as exc:
        return {
            "valid": False,
            "http_status": 0,
            "message": "Không kết nối được OneBSS: " + str(exc),
        }
    finally:
        if response is not None:
            response.close()


def main():
    if len(sys.argv) != 2:
        emit_result(success=False, message="Thiếu đường dẫn ekyc_full.py")
        return 2

    source_path = os.path.abspath(sys.argv[1])
    if not os.path.isfile(source_path):
        emit_result(success=False, message="Không tìm thấy ekyc_full.py")
        return 2

    try:
        request = read_request()
        module = load_source(source_path)
        action = str(request.get("action") or "status").strip().lower()

        if action == "status":
            emit_result(success=True, message="Đã đọc cấu hình", **auth_status(module))
            return 0

        if action == "ping_sources":
            cbss_ping = module._CBSS_RUNTIME.ping_saved_session()
            employ_ping = ping_onebss_session(module)
            state = auth_status(module)
            if cbss_ping.get("http_status") or "Chưa lưu" in cbss_ping.get("message", ""):
                state["cbssConfigured"] = bool(cbss_ping.get("valid"))
            if employ_ping.get("http_status") or "Chưa lưu" in employ_ping.get("message", ""):
                state["employConfigured"] = bool(employ_ping.get("valid"))
            emit_result(
                success=True,
                message=(
                    f"CBSS: {cbss_ping.get('message', '?')} | "
                    f"OneBSS: {employ_ping.get('message', '?')}"
                ),
                cbssPinged=True,
                cbssSessionValid=bool(cbss_ping.get("valid")),
                cbssHttpStatus=int(cbss_ping.get("http_status") or 0),
                cbssSessionMessage=str(cbss_ping.get("message") or ""),
                employPinged=True,
                employSessionValid=bool(employ_ping.get("valid")),
                employHttpStatus=int(employ_ping.get("http_status") or 0),
                employSessionMessage=str(employ_ping.get("message") or ""),
                **state,
            )
            return 0

        if action == "cbss_send_pin":
            username = str(request.get("username") or "").strip()
            module._CBSS_RUNTIME.base_url = str(
                request.get("baseUrl") or module.CBSS_BASE_URL).rstrip("/")
            message = module._CBSS_RUNTIME.send_pin(username)
            emit_result(success=True, message=str(message), **auth_status(module))
            return 0

        if action == "cbss_login":
            username = str(request.get("username") or "").strip()
            password = str(request.get("password") or "")
            otp = str(request.get("otp") or "").strip()
            module._CBSS_RUNTIME.base_url = str(
                request.get("baseUrl") or module.CBSS_BASE_URL).rstrip("/")
            module._CBSS_RUNTIME.login(username, password, otp, save=True)
            state = auth_status(module)
            state["cbssConfigured"] = True
            state["cbssUsername"] = username
            emit_result(success=True, message="Đăng nhập CBSS thành công", **state)
            return 0

        if action == "employ_begin":
            username = str(request.get("username") or "").strip()
            password = str(request.get("password") or "")
            if not username or not password:
                raise ValueError("Vui lòng nhập tài khoản và mật khẩu Employ")
            device_id = os.urandom(8).hex()
            response = module.requests.post(
                f"{module.ONEBSS_BASE}/quantri/user/xacthuc_tapdoan",
                headers={"Content-Type": "application/json", "Accept": "application/json"},
                json={
                    "username": username,
                    "password": password,
                    "os_type": "1",
                    "device_id": device_id,
                },
                timeout=60,
                verify=False,
            )
            payload = read_json_response(response, "Employ")
            if response.status_code != 200 or payload.get("error_code") != "BSS-00000000":
                raise RuntimeError(str(payload.get("message") or "Sai tài khoản hoặc mật khẩu Employ"))
            challenge = str((payload.get("data") or {}).get("secretCode") or "")
            if not challenge:
                raise RuntimeError("Employ không trả về mã phiên xác thực")
            emit_result(
                success=True,
                message="Đã gửi OTP Employ tới điện thoại/email",
                challenge=challenge,
                deviceId=device_id,
                **auth_status(module),
            )
            return 0

        if action == "employ_verify":
            username = str(request.get("username") or "").strip()
            password = str(request.get("password") or "")
            otp = str(request.get("otp") or "").strip()
            challenge = str(request.get("challenge") or "")
            device_id = str(request.get("deviceId") or "")
            if not otp or not challenge or not device_id:
                raise ValueError("Thiếu OTP hoặc phiên xác thực Employ")
            response = module.requests.post(
                f"{module.ONEBSS_BASE}/quantri/oauth/token",
                headers={"Content-Type": "application/json", "Accept": "application/json"},
                json={
                    "grant_type": "password",
                    "client_id": "clientapp",
                    "client_secret": "password",
                    "secretCode": challenge,
                    "otp": otp,
                },
                timeout=60,
                verify=False,
            )
            payload = read_json_response(response, "Employ")
            token = str(payload.get("access_token") or "")
            if response.status_code != 200 or not token:
                raise RuntimeError(str(payload.get("message") or "OTP Employ sai hoặc hết hạn"))
            module._ONEBSS_SESSION["token"] = token
            module._ONEBSS_SESSION["device_id"] = device_id
            module._ONEBSS_SESSION["logged_in"] = True
            module._save_config(
                token,
                device_id,
                username=username,
                password=password,
            )
            state = auth_status(module)
            state["employConfigured"] = True
            state["employUsername"] = username
            emit_result(success=True, message="Đăng nhập Employ thành công", **state)
            return 0

        if action == "employ_token":
            token = str(request.get("token") or "").strip()
            if token.lower().startswith("bearer "):
                token = token[7:].strip()
            if not token:
                raise ValueError("Vui lòng nhập token Employ/OneBSS")
            if not plausible_signed_access_token(token):
                raise ValueError("Token Employ không đúng định dạng JWT hoặc đã hết hạn")
            token_username, token_device_id = token_identity(token)
            old_config = module._load_saved_config()
            device_id = str(
                request.get("deviceId")
                or token_device_id
                or old_config.get("device_id")
                or module.ONEBSS_DEVICE_ID
                or os.urandom(8).hex()
            )
            response = module.requests.post(
                f"{module.ONEBSS_BASE}/quantri/user/thongtin_nv",
                headers=module._onebss_headers(token, device_id),
                json={},
                timeout=30,
                verify=False,
            )
            try:
                payload = response.json()
            except ValueError:
                payload = {}
            if not isinstance(payload, dict):
                payload = {}
            code = str(payload.get("error_code", payload.get("errorCode", "")))
            message = str(payload.get("message") or "").strip()
            if not message:
                message = " ".join(str(getattr(response, "text", "") or "").split())[:300]
            auth_text = f"{code} {message}".lower()
            rejected = (
                response.status_code in (401, 403)
                or any(marker in auth_text for marker in (
                    "token không hợp lệ", "token khong hop le",
                    "token hết hạn", "token het han", "unauthorized",
                    "expired", "bss-00000401", "bss-00000403",
                ))
            )
            # Some OneBSS gateways do not expose thongtin_nv and reply
            # "No handler found" even for a valid token. In that case accept
            # only a signed-looking unexpired JWT; the IC/OC permission API is
            # the authoritative validation before any mutation.
            profile_valid = response.status_code == 200 and not rejected
            if rejected:
                raise RuntimeError(message or f"Token Employ không hợp lệ (HTTP {response.status_code})")
            if not profile_valid and not plausible_signed_access_token(token):
                raise RuntimeError(message or f"Token Employ không hợp lệ (HTTP {response.status_code})")
            module._ONEBSS_SESSION["token"] = token
            module._ONEBSS_SESSION["device_id"] = device_id
            module._ONEBSS_SESSION["logged_in"] = True
            module._save_config(token, device_id, username=token_username)
            state = auth_status(module)
            state["employConfigured"] = True
            state["employUsername"] = token_username
            emit_result(success=True, message="Đăng nhập Employ bằng token thành công", **state)
            return 0

        raise ValueError("Thao tác đăng nhập nguồn ảnh không hợp lệ")
    except SystemExit as exc:
        emit_result(
            success=False,
            message=f"Nguồn ảnh dừng bất thường (mã {exc.code}). Hãy kiểm tra cấu hình Python/nguồn ảnh."
        )
        return 1
    except Exception as exc:
        message = str(exc)
        if "Expecting value" in message:
            message = (
                "Máy chủ không trả dữ liệu hợp lệ. "
                "Kiểm tra mạng, VPN/proxy hoặc máy chủ rồi thử lại."
            )
        emit_result(success=False, message=message)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
