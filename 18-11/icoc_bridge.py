# -*- coding: utf-8 -*-
"""CCBS lookup and OneBSS IC/OC bridge for ToolGSM.

The bridge receives one JSON request on stdin and emits progress protocol
lines followed by one result line. Credentials and tokens remain managed by the existing
``ekyc_full.py`` authentication runtime.
"""

from __future__ import annotations

import ast
import importlib.util
import json
import os
import re
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from typing import Any


RESULT_PREFIX = "TOOLGSM_ICOC_RESULT_JSON:"
PROGRESS_PREFIX = "TOOLGSM_ICOC_PROGRESS_JSON:"
MENU_ID = "11213"
SUCCESS_CODES = {"", "0", "200", "SUCCESS", "BSS-00000000"}
OPEN_VALUES = {"1", "MO", "OPEN", "ACTIVE", "HOAT_DONG"}
LOCKED_VALUES = {"0", "KHOA", "LOCK", "LOCKED", "CLOSED", "CAT", "CHAN"}


def configure_utf8_stdio() -> None:
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


def emit_protocol(prefix: str, value: dict[str, Any]) -> None:
    line = prefix + json.dumps(value, ensure_ascii=True) + "\n"
    buffer = getattr(sys.stdout, "buffer", None)
    if buffer is not None:
        buffer.write(line.encode("utf-8", errors="replace"))
        buffer.flush()
    else:
        print(line, end="", flush=True)


def emit_result(**value: Any) -> None:
    emit_protocol(RESULT_PREFIX, value)


def emit_progress(**value: Any) -> None:
    emit_protocol(PROGRESS_PREFIX, value)


def read_request() -> dict[str, Any]:
    raw = sys.stdin.readline().lstrip("\ufeff")
    if not raw.strip():
        raise ValueError("ToolGSM không gửi dữ liệu IC/OC cho Python")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("Dữ liệu IC/OC không hợp lệ")
    return value


def load_source(source_path: str):
    source_dir = os.path.dirname(source_path)
    if source_dir not in sys.path:
        sys.path.insert(0, source_dir)
    spec = importlib.util.spec_from_file_location("toolgsm_icoc_source", source_path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Không nạp được module DKTTTB")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def normalize_msisdn(value: Any) -> str:
    digits = re.sub(r"\D", "", str(value or ""))
    if re.fullmatch(r"84\d{9}", digits):
        return digits
    if re.fullmatch(r"0\d{9}", digits):
        return "84" + digits[1:]
    if re.fullmatch(r"\d{9}", digits):
        return "84" + digits
    raise ValueError("SĐT phải gồm 10 số đầu 0, 9 số hoặc 11 số đầu 84")


def normalize_state(value: Any) -> bool | None:
    if value is True or value == 1:
        return True
    if value is False or value == 0:
        return False
    normalized = str(value or "").strip().upper()
    if normalized in OPEN_VALUES:
        return True
    if normalized in LOCKED_VALUES:
        return False
    return None


def normalize_action(value: Any) -> str:
    normalized = str(value or "").strip().upper()
    return normalized if normalized in {"MO", "KHOA", "GIU"} else "MO"


def action_text(value: str) -> str:
    return {"MO": "Mở", "KHOA": "Đóng", "GIU": "Giữ nguyên"}[value]


def decode_js_string(raw: str, quote: str = '"') -> str:
    try:
        if quote == '"':
            return json.loads('"' + raw + '"')
        return ast.literal_eval("'" + raw.replace("'", "\\'") + "'")
    except (ValueError, SyntaxError, json.JSONDecodeError):
        return bytes(raw, "utf-8").decode("unicode_escape", errors="replace")


def decode_dwr_value(token: str, variables: dict[str, Any]) -> Any:
    value = token.strip()
    if re.fullmatch(r"s\d+", value):
        return variables.get(value, "")
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {'"', "'"}:
        return decode_js_string(value[1:-1], value[0])
    lowered = value.casefold()
    if lowered == "true":
        return True
    if lowered == "false":
        return False
    if lowered == "null":
        return None
    try:
        return float(value) if "." in value else int(value)
    except ValueError:
        return value


def parse_dwr(text: str) -> dict[str, Any]:
    variables: dict[str, Any] = {}
    for match in re.finditer(r'\bvar\s+(s\d+)\s*=\s*"((?:\\.|[^"\\])*)"\s*;', text):
        variables[match.group(1)] = decode_js_string(match.group(2), '"')
    for match in re.finditer(r"\bvar\s+(s\d+)\s*=\s*'((?:\\.|[^'\\])*)'\s*;", text):
        variables[match.group(1)] = decode_js_string(match.group(2), "'")
    for match in re.finditer(
        r"\bvar\s+(s\d+)\s*=\s*(true|false|null|-?\d+(?:\.\d+)?)\s*;",
        text,
        re.IGNORECASE,
    ):
        variables[match.group(1)] = decode_dwr_value(match.group(2), variables)

    result: dict[str, Any] = {}
    value_token = (
        r'(?:s\d+|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|'
        r'true|false|null|-?\d+(?:\.\d+)?)'
    )
    patterns = (
        (re.compile(rf's0\s*\[\s*"((?:\\.|[^"\\])*)"\s*\]\s*=\s*({value_token})\s*;', re.I), '"'),
        (re.compile(rf"s0\s*\[\s*'((?:\\.|[^'\\])*)'\s*\]\s*=\s*({value_token})\s*;", re.I), "'"),
        (re.compile(rf"s0\s*\.\s*([A-Za-z_$][\w$]*)\s*=\s*({value_token})\s*;", re.I), ""),
    )
    for pattern, quote in patterns:
        for match in pattern.finditer(text):
            key = decode_js_string(match.group(1), quote) if quote else match.group(1)
            result[str(key)] = decode_dwr_value(match.group(2), variables)
    return result


def clone_cbss_client(base_client):
    client = type(base_client)(base_client.base_url)
    client.session.cookies.update(base_client.session.cookies)
    client.username = base_client.username
    client._combined_password = base_client._combined_password
    client.logged_in = True
    client._auth_cookie_names = set(base_client._auth_cookie_names)
    client._login_response_text = base_client._login_response_text
    client._login_response_url = base_client._login_response_url
    client._main_html = base_client._main_html
    client._main_url = base_client._main_url
    return client


def lookup_ccbs(module, request: dict[str, Any]) -> dict[str, Any]:
    raw_items = request.get("items") or []
    if not isinstance(raw_items, list) or not raw_items:
        raise ValueError("Không có thuê bao để tra cứu CCBS")
    base_client = module._CBSS_RUNTIME.ensure_logged_in()
    worker_count = max(1, min(int(request.get("maxParallelism") or 4), 8, len(raw_items)))
    local = threading.local()

    def get_client():
        if not hasattr(local, "client"):
            local.client = clone_cbss_client(base_client)
        return local.client

    def lookup(item: Any) -> dict[str, Any]:
        port = str((item or {}).get("port") or "").strip()
        phone = str((item or {}).get("phone") or "").strip()
        try:
            msisdn = normalize_msisdn(phone)
            response = get_client().dwr(
                "NEORemoting",
                "getRec",
                f"neo.cmdv114.vinacore_new.layTTThueBao_v4('{msisdn}','0')",
            )
            parsed = parse_dwr(str(response.get("_raw") or ""))
            if not parsed:
                raise RuntimeError("CCBS không trả dữ liệu thuê bao")
            incoming = normalize_state(parsed.get("goi_den"))
            outgoing = normalize_state(parsed.get("goi_di"))
            return {
                "port": port,
                "phone": phone,
                "normalizedPhone": msisdn,
                "success": True,
                "incomingEnabled": incoming,
                "outgoingEnabled": outgoing,
                "eligibleForEmploy": incoming is False,
                "changed": False,
                "message": (
                    "Gọi đến = Không; đã đưa sang Employ"
                    if incoming is False
                    else "Gọi đến = Có" if incoming is True
                    else "CCBS không trả rõ trạng thái Gọi đến"
                ),
            }
        except Exception as exc:
            return {
                "port": port,
                "phone": phone,
                "success": False,
                "eligibleForEmploy": False,
                "changed": False,
                "message": str(exc),
            }

    indexed_results: list[tuple[int, dict[str, Any]]] = []
    with ThreadPoolExecutor(max_workers=worker_count) as pool:
        futures = {pool.submit(lookup, item): index for index, item in enumerate(raw_items)}
        for future in as_completed(futures):
            indexed_results.append((futures[future], future.result()))
    items = [value for _, value in sorted(indexed_results, key=lambda pair: pair[0])]
    eligible = sum(1 for item in items if item.get("eligibleForEmploy"))
    failed = sum(1 for item in items if not item.get("success"))
    return {
        "success": failed < len(items),
        "message": f"CCBS xong {len(items) - failed}/{len(items)}; Gọi đến = Không: {eligible}",
        "items": items,
    }


def onebss_headers(module, token: str, device_id: str) -> dict[str, str]:
    headers = dict(module._onebss_headers(token, device_id))
    headers["selectedmenuid"] = MENU_ID
    headers["SelectedMenuId"] = MENU_ID
    return headers


def api_succeeded(response, payload: Any) -> bool:
    if response.status_code < 200 or response.status_code >= 300:
        return False
    if payload in (None, ""):
        return True
    if not isinstance(payload, dict):
        return not re.search(r"\b(error|fail|exception|no_data)\b", str(payload), re.I)
    if payload.get("success") is False or payload.get("ok") is False:
        return False
    code = str(payload.get("error_code", payload.get("errorCode", "")) or "").strip().upper()
    if code not in SUCCESS_CODES:
        return False
    error = payload.get("error")
    if error not in (None, "", False):
        if str(error).strip().upper() not in SUCCESS_CODES:
            return False
    message = str(payload.get("message", payload.get("msg", "")) or "").strip()
    return re.fullmatch(r"NO_DATA|FAILED?|ERROR", message, re.I) is None


def failure_message(response, payload: Any, fallback: str) -> str:
    if isinstance(payload, dict):
        code = payload.get("error_code", payload.get("errorCode", payload.get("code", "")))
        message = payload.get("message", payload.get("msg", payload.get("detail", "")))
        detail = " - ".join(str(value).strip() for value in (code, message) if str(value or "").strip())
        if detail:
            return detail
    if isinstance(payload, str) and payload.strip():
        return payload.strip()[:400]
    return f"{fallback} (HTTP {response.status_code})"


def is_rate_limited(response, payload: Any) -> bool:
    if response.status_code == 429:
        return True
    if not isinstance(payload, dict):
        return False
    code = str(payload.get("error_code", payload.get("errorCode", "")) or "").strip().upper()
    return code == "BSS-00000500"


class OneBssClient:
    def __init__(self, module, token: str, device_id: str):
        self.module = module
        self.headers = onebss_headers(module, token, device_id)
        self._last_request_at = 0.0

    def _pace(self) -> None:
        remaining = 2.0 - (time.monotonic() - self._last_request_at)
        if remaining > 0:
            time.sleep(remaining)

    def post(self, path: str, body: dict[str, Any], *, timeout: int = 60):
        self._pace()
        response = None
        try:
            response = self.module.requests.post(
                f"{self.module.ONEBSS_BASE}{path}",
                headers=self.headers,
                json={**body, "menu_id": MENU_ID},
                timeout=timeout,
                verify=False,
            )
            self._last_request_at = time.monotonic()
            try:
                payload = response.json()
            except ValueError:
                payload = response.text
            return response, payload
        except Exception:
            self._last_request_at = time.monotonic()
            raise

    def post_read(self, path: str, body: dict[str, Any], *, timeout: int = 60):
        last_error = None
        for attempt in range(4):
            try:
                response, payload = self.post(path, body, timeout=timeout)
            except Exception as exc:
                last_error = exc
                if attempt >= 3:
                    raise
                time.sleep(0.5 * (attempt + 1))
                continue
            transient = (
                is_rate_limited(response, payload)
                or response.status_code in (408, 409)
                or response.status_code >= 500
            )
            if not transient or attempt >= 3:
                return response, payload
            retry_after = response.headers.get("Retry-After", "")
            try:
                delay = float(retry_after)
            except (TypeError, ValueError):
                delay = 30.0 if is_rate_limited(response, payload) else 0.5 * (attempt + 1)
            time.sleep(max(0.5, delay))
        raise RuntimeError(str(last_error or "Không gọi được OneBSS"))


def subscriber_data(payload: Any) -> dict[str, Any]:
    if not isinstance(payload, dict):
        return {}
    data = payload.get("data")
    return data if isinstance(data, dict) else payload


def find_state(data: dict[str, Any], names: tuple[str, ...]) -> bool | None:
    for name in names:
        if name in data:
            return normalize_state(data[name])
    return None


def unlock_employ(module, request: dict[str, Any]) -> dict[str, Any]:
    raw_items = request.get("items") or []
    note = str(request.get("note") or "").strip()
    delay_seconds = max(2, min(int(request.get("delaySeconds") or 20), 3600))
    ic_action = normalize_action(request.get("icAction"))
    oc_action = normalize_action(request.get("ocAction"))
    action_summary = f"IC={action_text(ic_action)}, OC={action_text(oc_action)}"
    if not isinstance(raw_items, list) or not raw_items:
        raise ValueError("Không có thuê bao để chạy Employ")
    if ic_action == "GIU" and oc_action == "GIU":
        raise ValueError("IC và OC đều Giữ nguyên; không có thao tác cần chạy")
    if not note:
        raise ValueError("Ghi chú thao tác IC/OC là bắt buộc")
    config = module._load_saved_config()
    token = str(config.get("token") or "")
    device_id = str(config.get("device_id") or module.ONEBSS_DEVICE_ID)
    if not token:
        raise RuntimeError("Chưa đăng nhập Employ/OneBSS")
    client = OneBssClient(module, token, device_id)

    emit_progress(
        completed=0,
        total=len(raw_items),
        port="",
        phone="",
        stage="Đang kiểm tra quyền CATMODICHVU...",
        waitSeconds=0,
    )
    permission_response, permission_payload = client.post_read(
        "/app-banhang/luong_didong_moi/mhddm_kiemtra_maquyen",
        {"ma_quyen": "CATMODICHVU"},
    )
    permission_data = subscriber_data(permission_payload)
    if (not api_succeeded(permission_response, permission_payload)
            or str(permission_data.get("status") or "").strip() != "1"):
        raise RuntimeError(failure_message(
            permission_response, permission_payload,
            "Tài khoản không có quyền CATMODICHVU"))

    results: list[dict[str, Any]] = []
    for item_index, raw_item in enumerate(raw_items):
        port = str((raw_item or {}).get("port") or "").strip()
        phone = str((raw_item or {}).get("phone") or "").strip()
        if item_index > 0:
            for remaining in range(delay_seconds, 0, -1):
                emit_progress(
                    completed=item_index,
                    total=len(raw_items),
                    port=port,
                    phone=phone,
                    stage=f"Chờ delay trước {port} / {phone}",
                    waitSeconds=remaining,
                )
                time.sleep(1)
        item: dict[str, Any] = {
            "port": port,
            "phone": phone,
            "success": False,
            "eligibleForEmploy": True,
            "changed": False,
        }
        try:
            msisdn = normalize_msisdn(phone)
            item["normalizedPhone"] = msisdn
            emit_progress(
                completed=item_index,
                total=len(raw_items),
                port=port,
                phone=phone,
                stage="Đang tra cứu trạng thái IC/OC...",
                waitSeconds=0,
            )
            status_response, status_payload = client.post_read(
                "/app-banhang/thuebaodidong/tracuu_tb_didong",
                {"p_so_tb": msisdn},
            )
            if not api_succeeded(status_response, status_payload):
                raise RuntimeError(failure_message(
                    status_response, status_payload,
                    "Không tra cứu được trạng thái IC/OC"))
            data = subscriber_data(status_payload)
            current_ic = find_state(data, ("goi_den", "GOI_DEN", "ic", "IC", "ic_status"))
            current_oc = find_state(data, ("goi_di", "GOI_DI", "oc", "OC", "oc_status"))
            item["incomingEnabled"] = current_ic
            item["outgoingEnabled"] = current_oc
            if current_ic is None or current_oc is None:
                raise RuntimeError("API Employ chưa trả đủ trạng thái IC/OC; không gửi lệnh")
            target_ic = current_ic if ic_action == "GIU" else ic_action == "MO"
            target_oc = current_oc if oc_action == "GIU" else oc_action == "MO"
            if current_ic == target_ic and current_oc == target_oc:
                item.update(
                    success=True,
                    changed=False,
                    message=f"Đã đúng trạng thái đích ({action_summary}); không cần đổi",
                )
                results.append(item)
                emit_progress(
                    completed=item_index + 1,
                    total=len(raw_items),
                    port=port,
                    phone=phone,
                    stage=item["message"],
                    waitSeconds=0,
                    item=item,
                )
                continue

            changes = []
            if ic_action != "GIU" and current_ic != target_ic:
                changes.append(f"ic:{1 if target_ic else 0}")
            if oc_action != "GIU" and current_oc != target_oc:
                changes.append(f"oc:{1 if target_oc else 0}")
            body = {
                "p_so_tb": msisdn,
                "p_goi_di": 1 if target_oc else 0,
                "p_goi_den": 1 if target_ic else 0,
                "p_ghi_chu": note,
                "p_loai_hinh": "1",
                "p_kieu_thuc_hien": "1,1",
                "p_dvthaydoi": ",".join(changes),
            }
            reconciled_after_timeout = False
            mutation_accepted = False
            last_mutation_error = ""
            for mutation_attempt in range(1, 4):
                emit_progress(
                    completed=item_index,
                    total=len(raw_items),
                    port=port,
                    phone=phone,
                    stage=f"Đang gửi lệnh {action_summary} (lần {mutation_attempt}/3)...",
                    waitSeconds=0,
                )
                uncertain = False
                rate_limited = False
                try:
                    change_response, change_payload = client.post(
                        "/app-banhang/thuebaodidong/khoamo_ic_oc",
                        body,
                        timeout=90,
                    )
                    if api_succeeded(change_response, change_payload):
                        mutation_accepted = True
                        break
                    rate_limited = is_rate_limited(change_response, change_payload)
                    uncertain = (
                        change_response.status_code in (408, 409)
                        or change_response.status_code >= 500
                    )
                    last_mutation_error = failure_message(
                        change_response, change_payload, "OneBSS lỗi")
                    if not rate_limited and not uncertain:
                        raise RuntimeError(last_mutation_error)
                except RuntimeError:
                    raise
                except Exception as exc:
                    uncertain = True
                    last_mutation_error = str(exc)

                if uncertain:
                    emit_progress(
                        completed=item_index,
                        total=len(raw_items),
                        port=port,
                        phone=phone,
                        stage="Phản hồi chưa rõ; đang tra lại trạng thái trước khi retry...",
                        waitSeconds=0,
                    )
                    try:
                        reconcile_response, reconcile_payload = client.post_read(
                            "/app-banhang/thuebaodidong/tracuu_tb_didong",
                            {"p_so_tb": msisdn},
                        )
                        if not api_succeeded(reconcile_response, reconcile_payload):
                            raise RuntimeError(failure_message(
                                reconcile_response,
                                reconcile_payload,
                                "Không đối soát được trạng thái IC/OC"))
                        reconciled = subscriber_data(reconcile_payload)
                        reconciled_ic = find_state(
                            reconciled,
                            ("goi_den", "GOI_DEN", "ic", "IC", "ic_status"),
                        )
                        reconciled_oc = find_state(
                            reconciled,
                            ("goi_di", "GOI_DI", "oc", "OC", "oc_status"),
                        )
                        if reconciled_ic is None or reconciled_oc is None:
                            raise RuntimeError("API đối soát chưa trả đủ trạng thái IC/OC")
                        item["incomingEnabled"] = reconciled_ic
                        item["outgoingEnabled"] = reconciled_oc
                        if reconciled_ic == target_ic and reconciled_oc == target_oc:
                            reconciled_after_timeout = True
                            break
                    except Exception as reconcile_exc:
                        raise RuntimeError(
                            "Lệnh có thể đã được nhận nhưng không đối soát được; "
                            "không retry mù: " + str(reconcile_exc)) from reconcile_exc

                if mutation_attempt >= 3:
                    raise RuntimeError(
                        "Tự thử lại 3 lần nhưng IC/OC vẫn chưa đạt trạng thái đích: "
                        + (last_mutation_error or "OneBSS chưa xác nhận"))

                retry_reason = (
                    "OneBSS giới hạn tần suất"
                    if rate_limited
                    else "Đối soát xác nhận IC/OC chưa đạt trạng thái đích"
                )
                for remaining in range(delay_seconds, 0, -1):
                    emit_progress(
                        completed=item_index,
                        total=len(raw_items),
                        port=port,
                        phone=phone,
                        stage=f"{retry_reason}; chuẩn bị retry lần {mutation_attempt + 1}/3",
                        waitSeconds=remaining,
                    )
                    time.sleep(1)

            if not mutation_accepted and not reconciled_after_timeout:
                raise RuntimeError(
                    "OneBSS chưa nhận lệnh thao tác IC/OC: "
                    + (last_mutation_error or "không rõ nguyên nhân"))

            if reconciled_after_timeout:
                item.update(
                    success=True,
                    changed=True,
                    message=f"Lệnh timeout nhưng đối soát xác nhận {action_summary} đã hoàn tất",
                )
            else:
                emit_progress(
                    completed=item_index,
                    total=len(raw_items),
                    port=port,
                    phone=phone,
                    stage="Đã gửi lệnh; đang xác minh trạng thái...",
                    waitSeconds=0,
                )
                verify_response, verify_payload = client.post_read(
                    "/app-banhang/thuebaodidong/tracuu_tb_didong",
                    {"p_so_tb": msisdn},
                )
                if not api_succeeded(verify_response, verify_payload):
                    raise RuntimeError(
                        "Đã gửi lệnh nhưng chưa xác minh được trạng thái: "
                        + failure_message(verify_response, verify_payload, "Lỗi xác minh"))
                verified = subscriber_data(verify_payload)
                verified_ic = find_state(verified, ("goi_den", "GOI_DEN", "ic", "IC", "ic_status"))
                verified_oc = find_state(verified, ("goi_di", "GOI_DI", "oc", "OC", "oc_status"))
                item["incomingEnabled"] = verified_ic
                item["outgoingEnabled"] = verified_oc
                if verified_ic != target_ic or verified_oc != target_oc:
                    raise RuntimeError(
                        f"OneBSS chưa xác nhận trạng thái đích ({action_summary})")
                item.update(
                    success=True,
                    changed=True,
                    message=f"Đã thực hiện {action_summary} và xác minh thành công",
                )
        except Exception as exc:
            item["message"] = str(exc)
        results.append(item)
        emit_progress(
            completed=item_index + 1,
            total=len(raw_items),
            port=port,
            phone=phone,
            stage=str(item.get("message") or "Đã xử lý"),
            waitSeconds=0,
            item=item,
        )

    succeeded = sum(1 for item in results if item.get("success"))
    return {
        "success": succeeded > 0,
        "message": f"Employ xong {succeeded}/{len(results)} thuê bao",
        "items": results,
    }


def main() -> int:
    if len(sys.argv) != 2:
        emit_result(success=False, message="Thiếu đường dẫn ekyc_full.py", items=[])
        return 2
    source_path = os.path.abspath(sys.argv[1])
    if not os.path.isfile(source_path):
        emit_result(success=False, message="Không tìm thấy ekyc_full.py", items=[])
        return 2
    try:
        request = read_request()
        module = load_source(source_path)
        action = str(request.get("action") or "").strip().lower()
        if action == "cbss_lookup":
            emit_result(**lookup_ccbs(module, request))
            return 0
        if action == "employ_unlock":
            emit_result(**unlock_employ(module, request))
            return 0
        raise ValueError("Thao tác IC/OC không hợp lệ")
    except SystemExit as exc:
        emit_result(success=False, message=f"Nguồn IC/OC dừng bất thường (mã {exc.code})", items=[])
        return 1
    except Exception as exc:
        emit_result(success=False, message=str(exc), items=[])
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
