import json
import os
import threading
import time
from urllib.parse import quote, unquote, urlparse

import requests


class ProxyConfigError(RuntimeError):
    pass


class RotatingProxyManager:
    """Quản lý proxy xoay ZingProxy và lưu cấu hình cạnh ứng dụng."""

    DEFAULTS = {
        "enabled": False,
        "key": "",
        "rotate_before_batch": True,
        "require_proxy": True,
        "proxy_raw": "",
        "last_change_at": 0.0,
        "cooldown_seconds": 240,
        "updated_at": "",
    }

    def __init__(self, config_path):
        self.config_path = config_path
        self.lock = threading.RLock()
        self.config = dict(self.DEFAULTS)
        self._load()
        if not os.path.exists(self.config_path):
            with self.lock:
                self._save_locked()

    def _load(self):
        with self.lock:
            try:
                with open(self.config_path, "r", encoding="utf-8") as handle:
                    data = json.load(handle)
                if isinstance(data, dict):
                    self.config.update(data)
            except (OSError, ValueError, TypeError):
                pass

    def _save_locked(self):
        os.makedirs(os.path.dirname(self.config_path), exist_ok=True)
        temp_path = self.config_path + ".tmp"
        with open(temp_path, "w", encoding="utf-8") as handle:
            json.dump(self.config, handle, ensure_ascii=False, indent=2)
        os.replace(temp_path, self.config_path)

    def snapshot(self):
        with self.lock:
            return dict(self.config)

    def update_settings(self, enabled, key, rotate_before_batch, require_proxy):
        with self.lock:
            old_key = str(self.config.get("key", "")).strip()
            new_key = self.normalize_key(key)
            self.config["enabled"] = bool(enabled and new_key)
            self.config["key"] = new_key
            self.config["rotate_before_batch"] = bool(rotate_before_batch)
            self.config["require_proxy"] = bool(require_proxy)
            if old_key != new_key:
                self.config["proxy_raw"] = ""
                self.config["last_change_at"] = 0.0
            self._save_locked()

    @staticmethod
    def normalize_key(value):
        text = str(value or "").strip()
        if not text:
            return ""
        if text.startswith(("http://", "https://")):
            path_parts = [part for part in urlparse(text).path.split("/") if part]
            if path_parts:
                return unquote(path_parts[-1]).strip()
        return text

    def is_enabled(self):
        with self.lock:
            return bool(self.config.get("enabled"))

    def is_required(self):
        with self.lock:
            return bool(self.config.get("enabled") and self.config.get("require_proxy", True))

    @staticmethod
    def _unwrap_response(payload):
        if not isinstance(payload, dict):
            raise ProxyConfigError("API proxy trả dữ liệu không hợp lệ")
        data = payload.get("data")
        if isinstance(data, dict):
            payload = data
        status = str(payload.get("status", "")).lower()
        status_code = int(payload.get("statusCode", 200) or 200)
        if status == "error" or status_code >= 400:
            raise ProxyConfigError(
                str(payload.get("error") or payload.get("message") or "API proxy báo lỗi")
            )
        return payload

    @staticmethod
    def _proxy_url(proxy_raw):
        raw = str(proxy_raw or "").strip()
        if not raw:
            raise ProxyConfigError("API chưa trả thông tin httpProxy")
        if raw.startswith(("http://", "https://")):
            return raw
        parts = raw.split(":", 3)
        if len(parts) != 4:
            raise ProxyConfigError("httpProxy phải có dạng IP:PORT:USERNAME:PASSWORD")
        host, port, username, password = parts
        if not host or not port or not username:
            raise ProxyConfigError("Thông tin httpProxy bị thiếu")
        return f"http://{quote(username, safe='')}:{quote(password, safe='')}@{host}:{port}"

    @classmethod
    def requests_proxies_from_raw(cls, proxy_raw):
        proxy_url = cls._proxy_url(proxy_raw)
        return {"http": proxy_url, "https": proxy_url}

    @staticmethod
    def masked_proxy(proxy_raw):
        raw = str(proxy_raw or "").strip()
        if not raw:
            return "Chưa lấy proxy"
        if raw.startswith(("http://", "https://")):
            return raw.split("@")[-1]
        parts = raw.split(":", 3)
        if len(parts) == 4:
            return f"{parts[0]}:{parts[1]}:{parts[2]}:******"
        return raw

    @staticmethod
    def _direct_get_json(url):
        session = requests.Session()
        session.trust_env = False
        response = session.get(url, timeout=20)
        response.raise_for_status()
        return response.json()

    def _key_locked(self):
        key = str(self.config.get("key", "")).strip()
        if not key:
            raise ProxyConfigError("Chưa nhập Key của proxy")
        return key

    def get_proxy(self):
        with self.lock:
            key = self._key_locked()
            payload = self._unwrap_response(
                self._direct_get_json(f"https://api.zingproxy.com/open/get-proxy/{quote(key, safe='')}")
            )
            proxy = payload.get("proxy")
            if not isinstance(proxy, dict):
                raise ProxyConfigError("API không trả đối tượng proxy")
            proxy_raw = proxy.get("httpProxy")
            self.requests_proxies_from_raw(proxy_raw)
            self.config["proxy_raw"] = proxy_raw
            cooldown = proxy.get("timeChangeAllowInSeconds")
            if cooldown is not None:
                self.config["cooldown_seconds"] = max(0, int(cooldown))
            self.config["updated_at"] = time.strftime("%Y-%m-%d %H:%M:%S")
            self._save_locked()
            return dict(proxy)

    def seconds_until_change(self):
        with self.lock:
            elapsed = time.time() - float(self.config.get("last_change_at", 0.0) or 0.0)
            cooldown = max(0, int(self.config.get("cooldown_seconds", 240) or 0))
            return max(0, int(cooldown - elapsed))

    def change_ip(self, force=False):
        with self.lock:
            wait_seconds = self.seconds_until_change()
            if wait_seconds and not force:
                proxy = self.get_proxy()
                return proxy, f"Chưa đổi IP: còn {wait_seconds} giây theo giới hạn nhà cung cấp"
            key = self._key_locked()
            payload = self._unwrap_response(
                self._direct_get_json(f"https://api.zingproxy.com/open/change-ip/{quote(key, safe='')}")
            )
            proxy = payload.get("proxy")
            if not isinstance(proxy, dict):
                raise ProxyConfigError("API đổi IP không trả đối tượng proxy")
            proxy_raw = proxy.get("httpProxy")
            if not proxy_raw:
                host = proxy.get("hostIp") or proxy.get("ip")
                port = proxy.get("portHttp")
                username = proxy.get("username")
                password = proxy.get("password")
                proxy_raw = f"{host}:{port}:{username}:{password}"
            self.requests_proxies_from_raw(proxy_raw)
            self.config["proxy_raw"] = proxy_raw
            self.config["last_change_at"] = time.time()
            self.config["cooldown_seconds"] = max(
                0, int(proxy.get("timeChangeAllowInSeconds", 240) or 0)
            )
            self.config["updated_at"] = time.strftime("%Y-%m-%d %H:%M:%S")
            self._save_locked()
            return dict(proxy), str(payload.get("message") or "Đổi IP thành công")

    def current_requests_proxies(self, refresh_if_missing=True):
        with self.lock:
            if not self.config.get("enabled"):
                return None
            proxy_raw = str(self.config.get("proxy_raw", "")).strip()
            if not proxy_raw and refresh_if_missing:
                self.get_proxy()
                proxy_raw = str(self.config.get("proxy_raw", "")).strip()
            return self.requests_proxies_from_raw(proxy_raw)

    def prepare_for_batch(self):
        with self.lock:
            if not self.config.get("enabled"):
                return None, "Proxy đang tắt"
            if self.config.get("rotate_before_batch", True):
                _proxy, message = self.change_ip(force=False)
            else:
                self.get_proxy()
                message = "Đã lấy thông tin proxy"
            return self.current_requests_proxies(refresh_if_missing=False), message

    def child_environment(self, base_environment=None):
        environment = dict(base_environment or os.environ)
        proxies = self.current_requests_proxies(refresh_if_missing=True)
        if proxies:
            environment["HTTP_PROXY"] = proxies["http"]
            environment["HTTPS_PROXY"] = proxies["https"]
        return environment

    def test_connection(self):
        proxies = self.current_requests_proxies(refresh_if_missing=True)
        if not proxies:
            raise ProxyConfigError("Proxy đang tắt")
        session = requests.Session()
        session.trust_env = False
        response = session.get("https://api.ipify.org?format=json", proxies=proxies, timeout=25)
        response.raise_for_status()
        payload = response.json()
        return str(payload.get("ip") or "Không xác định")
