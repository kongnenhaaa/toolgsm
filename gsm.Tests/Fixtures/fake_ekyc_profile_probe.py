"""Probe used to verify that ToolGSM preserves the OTP device profile."""


EXPECTED_PROFILE = {
    "di": "device-a|device-a|unknown|Android||3.3.99.Prd|CPH2179|11|",
    "model": "CPH2179",
    "android_ver": "11",
    "fcm_token": "fcm-a",
    "mac": "0123456789abcdef",
}


def _random_device_profile():
    return dict(EXPECTED_PROFILE)


class DhAdapter:
    pass


class _FakeResponse:
    status_code = 200

    @staticmethod
    def json():
        return {"error_code": "0", "message": "OTP accepted"}


class _FakeSession:
    def mount(self, prefix, adapter):
        if prefix != "https://" or not isinstance(adapter, DhAdapter):
            raise RuntimeError("DhAdapter was not configured")

    def post(self, url, headers, json, timeout, verify):
        if url.endswith("/otp_send"):
            if json != {"msisdn": "84912345678", "otp_service": "authen_msisdn"}:
                raise RuntimeError("otp_send payload was not used")
        elif url.endswith("/authen_msisdn"):
            if json.get("mode") != "otp" or json.get("password") != "123456":
                raise RuntimeError("OTP was not passed in password field")
            if "otp" in json:
                raise RuntimeError("legacy otp field was not translated")
        else:
            raise RuntimeError("unexpected endpoint")
        if headers.get("Device-Info") != EXPECTED_PROFILE["di"]:
            raise RuntimeError("wrong Device-Info")
        return _FakeResponse()


class requests:
    Session = _FakeSession


def run_ekyc_auto(phone, password="", otp="", use_local_img=True,
                  log_cb=print, tag=""):
    profile = _random_device_profile()
    if profile != EXPECTED_PROFILE:
        return False, "", phone, f"profile mismatch: {profile!r}"
    requests.Session().post(
        "https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_msisdn",
        headers={"Device-Info": profile["di"]},
        json={"mode": "otp", "otp": otp},
        timeout=60,
        verify=False)
    if otp != "123456":
        return False, "", phone, "otp mismatch"
    log_cb("PROFILE_REUSE_OK")
    return True, "TEST USER", phone, ""
