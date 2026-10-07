"""Offline fixture for the ToolGSM Employ two-step authentication bridge."""


ONEBSS_BASE = "https://offline.test"
CBSS_BASE_URL = "https://offline-cbss.test"
_ONEBSS_SESSION = {}
_saved_config = {}


class _CbssRuntime:
    base_url = CBSS_BASE_URL

    def __init__(self):
        self._accounts = []
        self._last_user = ""

    def saved_credentials(self):
        return list(self._accounts), self._last_user

    def send_pin(self, username):
        self._last_user = username
        return "Đã gửi OTP/PIN CBSS"

    def login(self, username, _password, _otp, save=True):
        self._last_user = username
        if save:
            self._accounts = [{"u": username}]


_CBSS_RUNTIME = _CbssRuntime()


class _Response:
    status_code = 200
    headers = {"Content-Type": "application/json; charset=utf-8"}

    def __init__(self, payload):
        self._payload = payload

    def json(self):
        return self._payload


class _Requests:
    def post(self, url, **_kwargs):
        if url.endswith("/quantri/user/xacthuc_tapdoan"):
            return _Response({
                "error_code": "BSS-00000000",
                "message": "Đã gửi mã xác thực",
                "data": {"secretCode": "offline-challenge"},
            })
        if url.endswith("/quantri/oauth/token"):
            return _Response({"access_token": "offline-access-token"})
        return _Response({"message": "Đường dẫn không hợp lệ"})


requests = _Requests()


def _load_saved_config():
    return dict(_saved_config)


def _save_config(token, device_id, username="", password=""):
    _saved_config.clear()
    _saved_config.update({
        "token": token,
        "device_id": device_id,
        "last_user": username,
        "accounts": [{"u": username, "p": password}],
    })
