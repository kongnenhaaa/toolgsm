"""Direct CBSS login and subscriber-image client used by ``ekyc_full.py``.

The OneBSS REST token and the legacy CBSS web session are different login
mechanisms.  Keeping this client separate prevents the UI from treating a
OneBSS token as if it were an authenticated CBSS browser session.
"""

import hashlib
import html
import json
import os
import re
import secrets
import threading
import time
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import quote, urljoin

import requests


CBSS_BASE_URL = "http://ccbs.vnpt.vn"
CBSS_LOGIN_PATH = "/ccbs/login.htm"
CBSS_MAIN_PATH = "/ccbs/main"
CBSS_IMAGE_MENU_ID = "m5462"
CBSS_IMAGE_MENU_ROUTE_FALLBACK = "vzzh5vxkvgoj57;7gmktz5y{hyixohkxyezzzh"
CBSS_MAX_DOCUMENT_BYTES = 50 * 1024 * 1024
CBSS_LOGIN_CF = ("1iutlomLork", "gjsot5pl{tizout")
CBSS_LOGIN_CH = ("1pl{tizout", "tku4ysgxz{o4rg\x7fu{z4ykz[ykxVgxgskzkx./")
CBSS_USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/150.0.0.0 Safari/537.36 Edg/150.0.0.0"
)


class _CbssFormParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.forms = []
        self._form = None
        self._select = None

    def handle_starttag(self, tag, attrs):
        values = {str(key).lower(): value or "" for key, value in attrs}
        tag = tag.lower()
        if tag == "form":
            self._form = {
                "action": values.get("action", ""),
                "method": values.get("method", "post").lower(),
                "controls": [],
            }
            self.forms.append(self._form)
        elif tag == "input" and self._form is not None:
            self._form["controls"].append(("input", values))
        elif tag == "select" and self._form is not None:
            self._select = {"name": values.get("name", ""), "value": ""}
        elif tag == "option" and self._select is not None:
            if "selected" in values or not self._select["value"]:
                self._select["value"] = values.get("value", "")

    def handle_endtag(self, tag):
        tag = tag.lower()
        if tag == "select" and self._form is not None and self._select:
            self._form["controls"].append(("select", dict(self._select)))
            self._select = None
        elif tag == "form":
            self._form = None


def normalize_number(value):
    digits = re.sub(r"\D+", "", str(value or ""))
    if digits.startswith("0084"):
        digits = digits[2:]
    if digits.startswith("84"):
        national = digits[2:]
    elif digits.startswith("0"):
        national = digits[1:]
    else:
        national = digits
    if not national:
        raise ValueError("Số thuê bao không hợp lệ.")
    return "84" + national, "0" + national


def _decode_js_string(raw, quote_char='"'):
    try:
        if quote_char == '"':
            return json.loads('"' + raw + '"')
        import ast

        return ast.literal_eval("'" + raw.replace("'", "\\'") + "'")
    except Exception:
        return bytes(raw, "utf-8").decode("unicode_escape", errors="replace")


def _extract_html(text):
    variables = {}
    for match in re.finditer(r'\bvar\s+(s\d+)\s*=\s*"((?:\\.|[^"\\])*)"\s*;', text):
        variables[match.group(1)] = _decode_js_string(match.group(2), '"')
    for match in re.finditer(r"\bvar\s+(s\d+)\s*=\s*'((?:\\.|[^'\\])*)'\s*;", text):
        variables[match.group(1)] = _decode_js_string(match.group(2), "'")
    for value in variables.values():
        candidate = html.unescape(str(value))
        if "<table" in candidate.lower():
            return candidate
    normalized = text.replace(r'\"', '"').replace(r"\'", "'")
    normalized = re.sub(
        r"\\u([0-9a-fA-F]{4})",
        lambda match: chr(int(match.group(1), 16)),
        normalized,
    )
    candidates = re.findall(r"<table\b[\s\S]*?</table>", html.unescape(normalized), re.I)
    return max(candidates, key=len) if candidates else ""


def _dwr_body(script, method, expression):
    encoded = str(expression).replace("%", "%25").replace('"', "%22").replace(",", "%2C")
    request_id = f"{secrets.randbelow(10000):04d}_{int(time.time() * 1000)}"
    return "\n".join(
        [
            "callCount=1",
            f"c0-scriptName={script}",
            f"c0-methodName={method}",
            f"c0-id={request_id}",
            f"c0-param0=string:{encoded}",
            "c0-param1=boolean:false",
            "xml=true",
        ]
    ) + "\n"


def _document_extension(content):
    if content.startswith(b"\xff\xd8\xff"):
        return ".jpg"
    if content.startswith(b"\x89PNG\r\n\x1a\n"):
        return ".png"
    if content.startswith((b"GIF87a", b"GIF89a")):
        return ".gif"
    if content.startswith(b"BM"):
        return ".bmp"
    if content.startswith((b"II*\x00", b"MM\x00*")):
        return ".tif"
    if len(content) >= 12 and content.startswith(b"RIFF") and content[8:12] == b"WEBP":
        return ".webp"
    return None


def _select_login_form(page_text):
    parser = _CbssFormParser()
    parser.feed(page_text)
    for form in parser.forms:
        names = {
            attrs.get("name", "").lower()
            for _kind, attrs in form["controls"]
            if attrs.get("name")
        }
        if "username" in names and "password" in names:
            return form
    if parser.forms:
        return parser.forms[0]
    raise RuntimeError("Không tìm thấy form đăng nhập CBSS.")


def _login_payload(form, username, combined_password):
    if len(combined_password) < 7 or not combined_password[-6:].isdigit():
        raise ValueError("OTP/PIN CBSS phải gồm đúng 6 chữ số.")
    payload = {}
    for kind, attrs in form["controls"]:
        name = attrs.get("name", "")
        if not name:
            continue
        if kind == "select":
            payload[name] = attrs.get("value", "")
            continue
        input_type = attrs.get("type", "text").lower()
        if input_type in {"checkbox", "radio"} and "checked" not in attrs:
            continue
        if input_type in {"text", "email", "password", "hidden", "submit"}:
            payload[name] = attrs.get("value", "")
    payload.pop("newpassword", None)
    payload.pop("retype", None)
    payload.pop("btnLogin", None)
    payload[CBSS_LOGIN_CF[0]] = CBSS_LOGIN_CF[1]
    payload[CBSS_LOGIN_CH[0]] = CBSS_LOGIN_CH[1]
    payload["username"] = username
    payload["password"] = combined_password[:-6]
    payload["options"] = combined_password[-6:]
    return payload


def load_accounts(path):
    path = Path(path)
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}, ""
    raw_accounts = payload.get("accounts", payload) if isinstance(payload, dict) else {}
    if not isinstance(raw_accounts, dict):
        return {}, ""
    accounts = {
        str(user): str(password)
        for user, password in raw_accounts.items()
        if str(user).strip() and isinstance(password, str)
    }
    last_username = str(payload.get("last_username", "")) if isinstance(payload, dict) else ""
    if last_username not in accounts:
        last_username = next(reversed(accounts), "")
    return accounts, last_username


def save_accounts(path, accounts, last_username):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "last_username": last_username if last_username in accounts else "",
        "accounts": dict(accounts),
    }
    part = path.with_name(f".{path.name}.{secrets.token_hex(4)}.part")
    try:
        part.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(part, path)
    finally:
        if part.exists():
            part.unlink()


def _save_browser_session(path, client):
    """Persist only the CBSS browser cookies needed after an app restart."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    cookies = []
    for cookie in client.session.cookies:
        cookies.append({
            "name": cookie.name,
            "value": cookie.value,
            "domain": cookie.domain or "",
            "path": cookie.path or "/",
            "secure": bool(cookie.secure),
            "expires": cookie.expires,
            "rest": dict(getattr(cookie, "_rest", {}) or {}),
        })
    payload = {
        "username": str(client.username or ""),
        "cookies": cookies,
    }
    part = path.with_name(f".{path.name}.{secrets.token_hex(4)}.part")
    try:
        part.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
        os.replace(part, path)
    finally:
        if part.exists():
            part.unlink()


def _load_browser_session(path, base_url, accounts):
    path = Path(path)
    try:
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return None
    if not isinstance(payload, dict):
        return None
    username = str(payload.get("username") or "").strip()
    cookies = payload.get("cookies")
    if not username or not isinstance(cookies, list) or not cookies:
        return None

    client = CbssDirectClient(base_url)
    for saved in cookies:
        if not isinstance(saved, dict):
            continue
        name = str(saved.get("name") or "")
        value = str(saved.get("value") or "")
        if not name:
            continue
        cookie = requests.cookies.create_cookie(
            name=name,
            value=value,
            domain=str(saved.get("domain") or ""),
            path=str(saved.get("path") or "/"),
            secure=bool(saved.get("secure")),
            expires=saved.get("expires"),
            rest=saved.get("rest") if isinstance(saved.get("rest"), dict) else {},
        )
        client.session.cookies.set_cookie(cookie)
    if not client.session.cookies:
        return None

    client.username = username
    client._combined_password = str(accounts.get(username) or "")
    client.logged_in = True
    client._auth_cookie_names = {cookie.name for cookie in client.session.cookies}
    return client


def split_combined_password(value):
    value = str(value or "")
    if len(value) >= 7 and value[-6:].isdigit():
        return value[:-6], value[-6:]
    return value, ""


class CbssDirectClient:
    def __init__(self, base_url=CBSS_BASE_URL):
        self.base_url = str(base_url).rstrip("/")
        self.session = requests.Session()
        self.session.trust_env = False
        self.session.headers.update(
            {
                "User-Agent": CBSS_USER_AGENT,
                "Accept": "*/*",
                "Accept-Language": "vi-VN,vi;q=0.9,en;q=0.8",
            }
        )
        self.logged_in = False
        self.username = ""
        self._combined_password = ""
        self._auth_lock = threading.RLock()
        self._auth_cookie_names = set()
        self._login_response_text = ""
        self._login_response_url = ""
        self._main_html = ""
        self._main_url = ""
        self._image_context_ready = False

    def send_pin(self, username):
        username = str(username or "").strip()
        if not username:
            raise ValueError("Vui lòng nhập username trước khi lấy OTP/PIN.")
        login_url = urljoin(self.base_url + "/", CBSS_LOGIN_PATH.lstrip("/"))
        page = self.session.get(login_url, timeout=30)
        page.raise_for_status()
        endpoint = urljoin(login_url, "sendpin?username=" + quote(username, safe=""))
        response = self.session.get(endpoint, headers={"Referer": login_url}, timeout=30)
        response.raise_for_status()
        message = " ".join(response.text.split())
        if not message:
            raise RuntimeError("CBSS không trả về thông báo sau khi lấy OTP/PIN.")
        return message

    def login(self, username, combined_password):
        with self._auth_lock:
            self._login_once(username, combined_password)

    def _login_once(self, username, combined_password):
        login_url = urljoin(self.base_url + "/", CBSS_LOGIN_PATH.lstrip("/"))
        page = self.session.get(login_url, timeout=30)
        page.raise_for_status()
        form = _select_login_form(page.text)
        response = self.session.post(
            urljoin(login_url, form.get("action") or CBSS_MAIN_PATH),
            data=_login_payload(form, username, combined_password),
            headers={
                "Accept": "*/*",
                "Origin": self.base_url,
                "Referer": login_url,
                "X-Requested-With": "XMLHttpRequest",
            },
            timeout=30,
            allow_redirects=True,
        )
        response.raise_for_status()
        ajax_result = response.text.strip()
        if ajax_result == "0":
            # The login form posts by AJAX and then navigates to ``var def``.
            # A bare GET /ccbs/main is not the same page and may have no menus.
            success_url = self._login_success_url(page.text, login_url)
            validation = self.session.get(
                success_url,
                headers={"Referer": login_url},
                timeout=30,
                allow_redirects=True,
            )
            validation.raise_for_status()
            for _ in range(2):
                if self._looks_logged_out(validation):
                    raise RuntimeError("CBSS vẫn yêu cầu đăng nhập sau khi trả mã thành công.")
                refresh_url = self._meta_refresh_url(validation.text)
                if not refresh_url:
                    break
                next_url = urljoin(validation.url, refresh_url)
                validation = self.session.get(
                    next_url,
                    headers={"Referer": validation.url},
                    timeout=30,
                    allow_redirects=True,
                )
                validation.raise_for_status()
            if self._looks_logged_out(validation):
                raise RuntimeError("CBSS vẫn yêu cầu đăng nhập sau khi trả mã thành công.")
            main_text, main_url = validation.text, validation.url
        elif ajax_result and not ajax_result.lstrip().startswith("<"):
            raise RuntimeError(self._login_error_message(ajax_result))
        elif self._looks_logged_out(response):
            raise RuntimeError("Đăng nhập CBSS chưa thành công; kiểm tra mật khẩu và OTP/PIN.")
        else:
            main_text, main_url = response.text, response.url
        self.username = username
        self._combined_password = combined_password
        self._login_response_text = main_text
        self._login_response_url = main_url
        self._main_html = main_text if CBSS_IMAGE_MENU_ID in main_text else ""
        self._main_url = main_url if self._main_html else ""
        self._image_context_ready = False
        self.logged_in = True
        self._auth_cookie_names = {cookie.name for cookie in self.session.cookies}

    @staticmethod
    def _login_success_url(page_text, login_url):
        match = re.search(
            r'''(?is)\bvar\s+def\s*=\s*(["'])((?:\\.|(?!\1).)*)\1''',
            page_text,
        )
        if not match:
            return urljoin(login_url, CBSS_MAIN_PATH.lstrip("/"))
        return urljoin(login_url, _decode_js_string(match.group(2), match.group(1)))

    def _browser_main_url(self):
        if self._login_response_url:
            return self._login_response_url
        # Restored cookies have no login response. Read the public login page
        # separately so its new cookie cannot replace the saved session.
        login_url = urljoin(self.base_url + "/", CBSS_LOGIN_PATH.lstrip("/"))
        with requests.Session() as probe:
            probe.trust_env = False
            page = probe.get(login_url, timeout=30)
            page.raise_for_status()
        return self._login_success_url(page.text, login_url)

    @staticmethod
    def _login_error_message(result):
        code = str(result).strip()
        messages = {
            "1": "Tài khoản CBSS không tồn tại.",
            "2": "Sai mật khẩu CBSS.",
            "3": "Tài khoản CBSS đã bị khóa.",
            "4": "OTP/PIN CBSS không đúng hoặc đã hết hạn. Hãy lấy mã mới.",
            "5": "Tài khoản chưa được cấp quyền truy cập CBSS trên HRM.",
            "10": "IP hiện tại không được phép truy cập CBSS.",
            "100": "Mật khẩu CBSS đã hết hạn; cần đổi mật khẩu trên trang CBSS.",
        }
        return f"CBSS từ chối đăng nhập (mã {code[:20]}): {messages.get(code, 'Máy chủ không chấp nhận thông tin đăng nhập.')}"

    @staticmethod
    def _meta_refresh_url(page_text):
        match = re.search(
            r'''(?is)<meta[^>]+http-equiv=["']?refresh["']?[^>]+content=["'][^"']*url=([^"'>; ]+)''',
            page_text,
        )
        return html.unescape(match.group(1)) if match else ""

    @staticmethod
    def _looks_logged_out(response, inspect_body=True):
        if response.status_code in {401, 403}:
            return True
        location = response.headers.get("Location", "").lower()
        if 300 <= response.status_code < 400 and (
            "login.htm" in location or "/ccbs/login" in location
        ):
            return True
        response_url = str(response.url).lower()
        if "login.htm" in response_url or response_url.endswith("/ccbs/login"):
            return True
        if not inspect_body or "html" not in response.headers.get("Content-Type", "").lower():
            return False
        body = response.text[:20000].lower()
        return (
            ("name=\"username\"" in body or "name='username'" in body)
            and ("name=\"password\"" in body or "name='password'" in body)
        )

    def _relogin_locked(self):
        if not self.username or not self._combined_password:
            self.logged_in = False
            raise RuntimeError("Phiên CBSS hết hạn và chưa lưu thông tin đăng nhập.")
        self.session.cookies.clear()
        self.logged_in = False
        self._main_html = ""
        self._main_url = ""
        self._image_context_ready = False
        self._login_once(self.username, self._combined_password)

    def _authenticated_request(self, method, url, **kwargs):
        kwargs.setdefault("allow_redirects", False)
        inspect_body = not bool(kwargs.get("stream"))
        for attempt in range(2):
            with self._auth_lock:
                if not self.logged_in:
                    self._relogin_locked()
                response = self.session.request(method, url, **kwargs)
            if not self._looks_logged_out(response, inspect_body):
                return response
            response.close()
            if attempt == 0:
                with self._auth_lock:
                    self._relogin_locked()
        self.logged_in = False
        raise RuntimeError("CBSS vẫn yêu cầu đăng nhập sau khi tự đăng nhập lại.")

    def _authenticated_main_page(self):
        if self._main_html:
            return self._main_html, self._main_url
        page_text = self._login_response_text
        page_url = self._login_response_url or self.base_url + CBSS_MAIN_PATH
        refresh_url = self._meta_refresh_url(page_text)
        if refresh_url:
            response = self._authenticated_request(
                "GET", urljoin(page_url, refresh_url), headers={"Referer": page_url}, timeout=30
            )
            response.raise_for_status()
            page_text, page_url = response.text, response.url
        if CBSS_IMAGE_MENU_ID not in page_text:
            response = self._authenticated_request(
                "GET",
                self._browser_main_url(),
                headers={"Referer": self.base_url + CBSS_LOGIN_PATH},
                timeout=30,
            )
            response.raise_for_status()
            page_text, page_url = response.text, response.url
        refresh_url = self._meta_refresh_url(page_text)
        if refresh_url:
            response = self._authenticated_request(
                "GET", urljoin(page_url, refresh_url),
                headers={"Referer": page_url}, timeout=30,
            )
            response.raise_for_status()
            page_text, page_url = response.text, response.url
        if CBSS_IMAGE_MENU_ID not in page_text:
            raise RuntimeError(
                "Đã đăng nhập CBSS nhưng không thấy menu cập nhật thuê bao "
                f"({CBSS_IMAGE_MENU_ID}); hãy kiểm tra quyền menu của tài khoản."
            )
        self._main_html, self._main_url = page_text, page_url
        return page_text, page_url

    def keep_alive(self):
        with self._auth_lock:
            if not self.logged_in:
                self._relogin_locked()
            url = self._browser_main_url()
            response = self._authenticated_request(
                "GET", url,
                headers={"Referer": self.base_url + CBSS_LOGIN_PATH},
                timeout=30,
            )
            response.raise_for_status()
            self._login_response_text = response.text
            self._login_response_url = response.url
            if CBSS_IMAGE_MENU_ID in response.text:
                self._main_html, self._main_url = response.text, response.url
        return "active"

    def dwr(self, script, method, expression):
        response = self._authenticated_request(
            "POST",
            f"{self.base_url}/ccbs/dwr/exec/{script}.{method}.dwr",
            data=_dwr_body(script, method, expression).encode("utf-8"),
            headers={
                "Content-Type": "text/plain",
                "Origin": self.base_url,
                "Referer": self.base_url + CBSS_MAIN_PATH,
                "Accept-Encoding": "gzip, deflate",
            },
            timeout=30,
        )
        response.raise_for_status()
        return {"_html": _extract_html(response.text), "_raw": response.text}

    def _activate_image_context(self):
        if self._image_context_ready:
            return self.base_url + CBSS_MAIN_PATH
        main_html, main_url = self._authenticated_main_page()
        route_match = re.search(
            rf'''(?is)\blink\(\s*["']{re.escape(CBSS_IMAGE_MENU_ID)}["']\s*,\s*["']([^"']+)''',
            main_html,
        )
        menu_route = html.unescape(route_match.group(1)) if route_match else CBSS_IMAGE_MENU_ROUTE_FALLBACK
        parser = _CbssFormParser()
        parser.feed(main_html)
        navigation_form = next(
            (
                form
                for form in parser.forms
                if any(attrs.get("name") == "1iutlomLork" for _kind, attrs in form["controls"])
            ),
            None,
        )
        if navigation_form is None:
            raise RuntimeError("Không tìm thấy form mở menu ảnh CBSS.")
        payload = {
            attrs.get("name", ""): attrs.get("value", "")
            for _kind, attrs in navigation_form["controls"]
            if attrs.get("name")
        }
        payload["1iutlomLork"] = menu_route
        response = self._authenticated_request(
            "POST",
            urljoin(self.base_url + "/", navigation_form.get("action") or CBSS_MAIN_PATH),
            data=payload,
            headers={"Origin": self.base_url, "Referer": main_url},
            timeout=30,
        )
        response.raise_for_status()
        if "frmViewImg" not in response.text:
            raise RuntimeError("CBSS không mở được màn hình ảnh thuê bao.")
        self._image_context_ready = True
        return response.url

    @staticmethod
    def _document_tokens(list_html):
        tokens = []
        pattern = re.compile(r'''viewFile\(\s*(["'])((?:\\.|(?!\1).)*)\1\s*\)''', re.I | re.S)
        for match in pattern.finditer(list_html):
            token = html.unescape(_decode_js_string(match.group(2), match.group(1)))
            if token and token not in tokens:
                tokens.append(token)
        return tokens

    @staticmethod
    def _image_urls(viewer_html):
        urls = []
        for match in re.finditer(r'''[^\s"'<>]*imageViewer\.jsp\?img=[^\s"'<>)]*''', viewer_html, re.I):
            value = html.unescape(match.group(0)).replace("\\/", "/")
            if value and value not in urls:
                urls.append(value)
        return urls

    def list_document_files(self, subscriber):
        international, _local = normalize_number(subscriber)
        menu_url = self._activate_image_context()
        page = self._authenticated_request(
            "GET",
            self.base_url + CBSS_MAIN_PATH,
            params={"iutlomLork": "pttb/prepaid/frmViewImg", "yuzh": international},
            headers={"Referer": menu_url},
            timeout=30,
        )
        page.raise_for_status()
        result = self.dwr(
            "DataRemoting", "getDoc", f'sbf.prepaid.layds_file_Personal("{international}")'
        )
        return self._document_tokens(result.get("_html", ""))

    def download_document_images(self, subscriber, selected_directory):
        international, _local = normalize_number(subscriber)
        tokens = self.list_document_files(international)
        destination = Path(selected_directory).resolve()
        destination.mkdir(parents=True, exist_ok=True)
        files, hashes = [], set()
        duplicate_count = invalid_count = 0
        for token in tokens:
            viewer = self._authenticated_request(
                "GET",
                self.base_url + CBSS_MAIN_PATH,
                params={"iutlomLork": "pttb/prepaid/frmViewFile", "lorktgsk": token},
                headers={"Referer": self.base_url + CBSS_MAIN_PATH},
                timeout=30,
            )
            viewer.raise_for_status()
            found = False
            for image_url in self._image_urls(viewer.text):
                response = self._authenticated_request(
                    "GET",
                    urljoin(viewer.url, image_url),
                    headers={"Referer": viewer.url, "Accept": "image/*,*/*;q=0.8"},
                    timeout=30,
                    stream=True,
                )
                try:
                    response.raise_for_status()
                    data = bytearray()
                    for chunk in response.iter_content(64 * 1024):
                        if chunk:
                            data.extend(chunk)
                        if len(data) > CBSS_MAX_DOCUMENT_BYTES:
                            raise RuntimeError("Một ảnh CBSS vượt giới hạn 50 MB.")
                finally:
                    response.close()
                content = bytes(data)
                extension = _document_extension(content)
                if not extension:
                    continue
                found = True
                digest = hashlib.sha256(content).hexdigest()
                if digest in hashes:
                    duplicate_count += 1
                    continue
                hashes.add(digest)
                number = len(files) + 1
                suffix = "_front" if number == 1 else ("_back" if number == 2 else ("" if number == 3 else f"_{number:02d}"))
                target = destination / f"{international}{suffix}{extension}"
                target.write_bytes(content)
                files.append(target)
            if not found:
                invalid_count += 1
        return {
            "directory": destination,
            "files": files,
            "references": len(tokens),
            "duplicates": duplicate_count,
            "invalid": invalid_count,
        }

    def download_portrait_image(self, subscriber, selected_directory):
        """Download only document/image number 3 (subscriber portrait).

        The first two CBSS document references are CCCD sides. They are not
        opened or downloaded here.
        """
        international, _local = normalize_number(subscriber)
        tokens = self.list_document_files(international)
        if len(tokens) < 3:
            raise RuntimeError(
                f"CBSS không có ảnh chân dung thứ 3 (tham chiếu={len(tokens)})."
            )
        destination = Path(selected_directory).resolve()
        destination.mkdir(parents=True, exist_ok=True)
        token = tokens[2]
        viewer = self._authenticated_request(
            "GET",
            self.base_url + CBSS_MAIN_PATH,
            params={"iutlomLork": "pttb/prepaid/frmViewFile", "lorktgsk": token},
            headers={"Referer": self.base_url + CBSS_MAIN_PATH},
            timeout=30,
        )
        viewer.raise_for_status()
        for image_url in self._image_urls(viewer.text):
            response = self._authenticated_request(
                "GET",
                urljoin(viewer.url, image_url),
                headers={"Referer": viewer.url, "Accept": "image/*,*/*;q=0.8"},
                timeout=30,
                stream=True,
            )
            try:
                response.raise_for_status()
                data = bytearray()
                for chunk in response.iter_content(64 * 1024):
                    if chunk:
                        data.extend(chunk)
                    if len(data) > CBSS_MAX_DOCUMENT_BYTES:
                        raise RuntimeError("Ảnh chân dung CBSS vượt giới hạn 50 MB.")
            finally:
                response.close()
            content = bytes(data)
            extension = _document_extension(content)
            if not extension or len(content) <= 2048:
                continue
            target = destination / f"{international}{extension}"
            target.write_bytes(content)
            return {
                "directory": destination,
                "path": target,
                "bytes": len(content),
                "references": len(tokens),
            }
        raise RuntimeError("CBSS không tải được nội dung ảnh chân dung thứ 3.")


class CbssRuntime:
    """Thread-safe, on-demand CBSS session shared by the UI and eKYC workers."""

    def __init__(self, accounts_path, base_url=CBSS_BASE_URL):
        self.accounts_path = str(accounts_path)
        self.session_path = str(Path(accounts_path).with_name("cbss_session.json"))
        self.base_url = str(base_url).rstrip("/")
        self._client = None
        self._lock = threading.RLock()
        self._login_lock = threading.RLock()
        self._download_lock = threading.Lock()
        self._listeners = []
        self._status = "CBSS: chưa đăng nhập"

    @property
    def status(self):
        with self._lock:
            return self._status

    @property
    def logged_in(self):
        with self._lock:
            return bool(self._client is not None and self._client.logged_in)

    def subscribe(self, callback):
        with self._lock:
            if callback not in self._listeners:
                self._listeners.append(callback)
            status = self._status
        callback(status)

    def _publish(self, status):
        with self._lock:
            self._status = str(status)
            listeners = list(self._listeners)
        for callback in listeners:
            try:
                callback(self._status)
            except Exception:
                pass

    def saved_credentials(self):
        return load_accounts(self.accounts_path)

    def has_saved_session(self):
        try:
            payload = json.loads(Path(self.session_path).read_text(encoding="utf-8-sig"))
            return bool(
                isinstance(payload, dict)
                and str(payload.get("username") or "").strip()
                and isinstance(payload.get("cookies"), list)
                and payload["cookies"]
            )
        except Exception:
            return False

    def _clear_saved_session(self):
        try:
            Path(self.session_path).unlink(missing_ok=True)
        except OSError:
            pass

    def _persist_client_session(self, client):
        try:
            _save_browser_session(self.session_path, client)
        except Exception:
            # Cookie persistence is optional; a successful live login remains usable.
            pass

    def _restore_saved_session(self):
        accounts, _last_username = load_accounts(self.accounts_path)
        client = _load_browser_session(self.session_path, self.base_url, accounts)
        if client is None:
            return None
        try:
            client.keep_alive()
        except Exception:
            client.session.close()
            self._clear_saved_session()
            return None
        self._persist_client_session(client)
        return client

    def ping_saved_session(self):
        """Validate the persisted cookie without falling back to OTP login."""
        accounts, _last_username = load_accounts(self.accounts_path)
        client = _load_browser_session(self.session_path, self.base_url, accounts)
        if client is None:
            return {
                "valid": False,
                "http_status": 0,
                "message": "Chưa lưu cookie phiên CBSS",
            }

        response = None
        try:
            response = client.session.get(
                self.base_url + CBSS_MAIN_PATH,
                headers={"Referer": self.base_url + CBSS_LOGIN_PATH},
                timeout=30,
                allow_redirects=True,
            )
            status = int(response.status_code or 0)
            logged_out = client._looks_logged_out(response, inspect_body=True)
            if status == 200 and not logged_out:
                client.logged_in = True
                with self._lock:
                    old_client = self._client
                    self._client = client
                if old_client is not None and old_client is not client:
                    old_client.session.close()
                self._persist_client_session(client)
                self._publish(f"CBSS: HTTP 200 - phiên còn hiệu lực ({client.username})")
                return {
                    "valid": True,
                    "http_status": status,
                    "message": "HTTP 200 - phiên CBSS còn hiệu lực",
                }

            if logged_out or status in (401, 403):
                self._clear_saved_session()
                with self._lock:
                    if self._client is client:
                        self._client = None
                message = f"HTTP {status} - phiên CBSS đã hết hạn"
            else:
                message = f"HTTP {status} - CBSS không trả trạng thái phiên hợp lệ"
            client.session.close()
            self._publish(f"CBSS: {message}")
            return {
                "valid": False,
                "http_status": status,
                "message": message,
            }
        except Exception as exc:
            client.session.close()
            message = "Không kết nối được CBSS: " + str(exc)
            self._publish(f"CBSS: {message}")
            return {
                "valid": False,
                "http_status": 0,
                "message": message,
            }
        finally:
            if response is not None:
                response.close()

    def send_pin(self, username):
        self._publish("CBSS: đang lấy OTP/PIN...")
        client = CbssDirectClient(self.base_url)
        message = client.send_pin(username)
        self._publish(f"CBSS: {message}")
        return message

    def login(self, username, password, otp, save=True):
        username = str(username or "").strip()
        password = str(password or "")
        otp = str(otp or "").strip()
        if not username or not password:
            raise ValueError("Vui lòng nhập username và mật khẩu CBSS.")
        if not re.fullmatch(r"\d{6}", otp):
            raise ValueError("OTP/PIN CBSS phải gồm đúng 6 chữ số.")
        with self._login_lock:
            self._publish("CBSS: đang đăng nhập...")
            client = CbssDirectClient(self.base_url)
            client.login(username, password + otp)
            client.keep_alive()
            with self._lock:
                self._client = client
            if save:
                accounts, _last = load_accounts(self.accounts_path)
                accounts[username] = password + otp
                save_accounts(self.accounts_path, accounts, username)
            self._persist_client_session(client)
            self._publish(f"CBSS: đã đăng nhập ({username})")
            return client

    def ensure_logged_in(self):
        with self._login_lock:
            with self._lock:
                client = self._client
            if client is not None and client.logged_in:
                return client
            client = self._restore_saved_session()
            if client is not None:
                with self._lock:
                    self._client = client
                self._publish(f"CBSS: đã khôi phục phiên ({client.username})")
                return client
            accounts, last_username = load_accounts(self.accounts_path)
            combined = accounts.get(last_username, "")
            password, otp = split_combined_password(combined)
            if not last_username or not password or not otp:
                raise RuntimeError("Chưa đăng nhập hoặc lưu tài khoản CBSS.")
            return self.login(last_username, password, otp, save=False)

    def download_images(self, subscriber, selected_directory):
        with self._download_lock:
            client = self.ensure_logged_in()
            result = client.download_document_images(subscriber, selected_directory)
            self._persist_client_session(client)
        self._publish(f"CBSS: vừa tải ảnh cho {normalize_number(subscriber)[1]}")
        return result

    def download_portrait(self, subscriber, selected_directory):
        with self._download_lock:
            client = self.ensure_logged_in()
            result = client.download_portrait_image(subscriber, selected_directory)
            self._persist_client_session(client)
        self._publish(f"CBSS: vừa tải ảnh mặt cho {normalize_number(subscriber)[1]}")
        return result

    def logout(self):
        with self._lock:
            if self._client is not None:
                self._client.session.close()
            self._client = None
        self._clear_saved_session()
        self._publish("CBSS: đã đăng xuất")
