"""Offline eKYC module that fails with only a spaced summary line."""


class _Session:
    def post(self, *_args, **_kwargs):
        raise AssertionError("Offline run fixture must not call HTTP")


class _Requests:
    def Session(self):
        return _Session()


requests = _Requests()


def _random_device_profile():
    return {
        "di": "fixture-device-info",
        "model": "fixture-model",
        "android_ver": "13",
        "fcm_token": "fixture-fcm",
        "mac": "00:00:00:00:00:00",
    }


def run_ekyc_auto(_phone, password="", otp="", use_local_img=True,
                  log_cb=print, tag=""):
    log_cb("verifyImeiChange: FAILED")
    return False, "", "", ""
