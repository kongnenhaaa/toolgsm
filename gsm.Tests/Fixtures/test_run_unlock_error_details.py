import importlib.util
import pathlib
import unittest


BRIDGE_PATH = (
    pathlib.Path(__file__).resolve().parents[2]
    / "gsm"
    / "device_unlock"
    / "run_unlock.py"
)
SPEC = importlib.util.spec_from_file_location("toolgsm_run_unlock_test", BRIDGE_PATH)
TARGET = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TARGET)


class RunUnlockErrorDetailTests(unittest.TestCase):
    def test_extracts_exact_error_code_and_message_from_system_response(self):
        priority, detail = TARGET.extract_ekyc_failure_detail(
            'Response: {"errorCode":"1314","message":"Ảnh khuôn mặt không hợp lệ"}'
        )

        self.assertEqual(100, priority)
        self.assertEqual(
            "errorCode=1314 | Ảnh khuôn mặt không hợp lệ", detail
        )

    def test_extracts_nonstandard_failed_line(self):
        priority, detail = TARGET.extract_ekyc_failure_detail(
            "FAILED: 709 - Thuê bao không đủ điều kiện"
        )

        self.assertEqual(100, priority)
        self.assertEqual("709 - Thuê bao không đủ điều kiện", detail)

    def test_redacts_credential_assignments(self):
        _priority, detail = TARGET.extract_ekyc_failure_detail(
            "verifyImeiChange ERROR: token=abc123 request failed"
        )

        self.assertNotIn("abc123", detail)
        self.assertIn("token=[REDACTED]", detail)

    def test_extracts_spaced_verify_summary_failure(self):
        priority, detail = TARGET.extract_ekyc_failure_detail(
            "verifyImeiChange: FAILED"
        )

        self.assertEqual(40, priority)
        self.assertEqual("verifyImeiChange: FAILED", detail)

    def test_extracts_unknown_http_failure(self):
        priority, detail = TARGET.extract_ekyc_failure_detail(
            "Upload face returned HTTP: 502"
        )

        self.assertEqual(35, priority)
        self.assertEqual("Upload face returned HTTP: 502", detail)

    def test_keeps_safe_unknown_last_output(self):
        detail = TARGET.fallback_ekyc_output_detail(
            "Hệ thống dừng tại bước đối soát khuôn mặt"
        )

        self.assertEqual("Hệ thống dừng tại bước đối soát khuôn mặt", detail)


if __name__ == "__main__":
    unittest.main(verbosity=2)
