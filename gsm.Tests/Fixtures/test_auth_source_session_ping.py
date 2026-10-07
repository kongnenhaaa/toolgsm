import importlib.util
import unittest
from pathlib import Path
from types import SimpleNamespace


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "gsm" / "device_unlock" / "auth_source.py"
SPEC = importlib.util.spec_from_file_location("toolgsm_auth_source_test", SOURCE)
AUTH_SOURCE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUTH_SOURCE)


class _FakeResponse:
    def __init__(self, status_code, payload):
        self.status_code = status_code
        self.payload = payload
        self.closed = False

    def json(self):
        return self.payload

    def close(self):
        self.closed = True


class _FakeRequests:
    response = None
    error = None

    @classmethod
    def post(cls, *_args, **_kwargs):
        if cls.error is not None:
            raise cls.error
        return cls.response


def _fake_module(token="saved-token"):
    return SimpleNamespace(
        _load_saved_config=lambda: {
            "token": token,
            "device_id": "saved-device",
        },
        _onebss_headers=lambda *_args: {"Content-Type": "application/json"},
        ONEBSS_BASE="https://onebss.test",
        ONEBSS_DEVICE_ID="default-device",
        requests=_FakeRequests,
    )


class OneBssSessionPingTests(unittest.TestCase):
    def tearDown(self):
        _FakeRequests.response = None
        _FakeRequests.error = None

    def test_http_200_reports_valid_session_and_closes_response(self):
        response = _FakeResponse(200, {"error_code": "0", "message": "OK"})
        _FakeRequests.response = response

        result = AUTH_SOURCE.ping_onebss_session(_fake_module())

        self.assertTrue(result["valid"])
        self.assertEqual(200, result["http_status"])
        self.assertTrue(response.closed)

    def test_token_rejection_reports_expired_session(self):
        response = _FakeResponse(
            401,
            {"error_code": "BSS-00000401", "message": "Token không hợp lệ."},
        )
        _FakeRequests.response = response

        result = AUTH_SOURCE.ping_onebss_session(_fake_module())

        self.assertFalse(result["valid"])
        self.assertEqual(401, result["http_status"])
        self.assertIn("hết hạn", result["message"])
        self.assertNotIn("saved-token", result["message"])
        self.assertTrue(response.closed)

    def test_missing_token_does_not_call_server(self):
        result = AUTH_SOURCE.ping_onebss_session(_fake_module(token=""))

        self.assertFalse(result["valid"])
        self.assertEqual(0, result["http_status"])
        self.assertIn("Chưa lưu token", result["message"])

    def test_network_error_is_not_reported_as_expired(self):
        _FakeRequests.error = OSError("offline")

        result = AUTH_SOURCE.ping_onebss_session(_fake_module())

        self.assertFalse(result["valid"])
        self.assertEqual(0, result["http_status"])
        self.assertIn("Không kết nối", result["message"])


if __name__ == "__main__":
    unittest.main()
