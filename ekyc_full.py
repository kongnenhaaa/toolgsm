"""
ekyc_full.py - My VNPT eKYC Complete Flow
==========================================
STEP 1: getChallengeCodeSdkEkyc (My VNPT API)
STEP 2: Upload face images + Liveness detection (VNPT IDG API)
STEP 3: (TODO) saveLogEkyc

Confirmed from: Frida hooks + HTTP Toolkit + Source code analysis
"""
import base64, hashlib, json, uuid, subprocess, re, secrets, string
import os, sys, requests, urllib3
from collections import OrderedDict
from Crypto.Cipher import AES
from Crypto.Util.Padding import unpad, pad
from Crypto.PublicKey import RSA
from Crypto.Signature import pkcs1_15
from Crypto.Hash import SHA256
from cryptography.hazmat.primitives.asymmetric import padding as asym_padding
from cryptography.hazmat.primitives.serialization import load_der_public_key
from cryptography.hazmat.backends import default_backend
from requests.adapters import HTTPAdapter
from urllib3.util.ssl_ import create_urllib3_context
import ssl

urllib3.disable_warnings()

# ===========================================================================
# CONFIG (all confirmed from Frida)
# ===========================================================================
ADB             = r"C:\Users\congn\AppData\Local\Android\Sdk\platform-tools\adb.exe"
AES_MASTER_KEY  = "dqeqgig123ndb123dsfsdf23g4d56jfq"   # getKeyEncryptAES()
SECRET_AUTHEN   = "05ca696sdg4533250c49bdf16a76ad247"    # getSecretAuthenKeySimOnline()
BEARER_TOKEN    = "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b"
DEVICE_INFO     = "6a66365c-8a19-47f0-bf7a-8f206869d0de|6a66365c-8a19-47f0-bf7a-8f206869d0de|unknown|Android||3.3.99.Prd|SM-S901B|13|"
MAC_ADDRESS     = "a9494f35748322ab"
CLIENT_SESSION  = f"ANDROID_SM-S901B_32_Device_3.6.6_{MAC_ADDRESS}_1783369872502_com.vnp.myvinaphone"
MYVNPT_BASE     = "https://api-myvnpt.vnpt.vn"
IDG_BASE        = "https://api.idg.vnpt.vn"
IV_ZEROS        = bytes(16)

# ===========================================================================
# CRYPTO HELPERS
# ===========================================================================
def aes_decrypt(b64_data, key=AES_MASTER_KEY):
    raw = base64.b64decode(b64_data)
    cipher = AES.new(key.encode("utf-8"), AES.MODE_CBC, IV_ZEROS)
    return unpad(cipher.decrypt(raw), AES.block_size).decode("utf-8")

def aes_encrypt(plaintext, key):
    """AES/CBC/PKCS7 encrypt (encryptAESGenerator)"""
    cipher = AES.new(key.encode("utf-8"), AES.MODE_CBC, IV_ZEROS)
    return base64.b64encode(
        cipher.encrypt(pad(plaintext.encode("utf-8"), AES.block_size))
    ).decode("utf-8")

def aes_decrypt_gen(b64_data, key):
    """AES/CBC/PKCS7 decrypt (decryptAESGenerator)"""
    raw = base64.b64decode(b64_data)
    cipher = AES.new(key.encode("utf-8"), AES.MODE_CBC, IV_ZEROS)
    return unpad(cipher.decrypt(raw), AES.block_size).decode("utf-8")

def sign_sha256_rsa(data_str, priv_key_b64):
    """signWithRSA - SHA256withRSA PKCS1v15"""
    key = RSA.import_key(base64.b64decode(priv_key_b64))
    h = SHA256.new(data_str.encode("utf-8"))
    return base64.b64encode(pkcs1_15.new(key).sign(h)).decode("utf-8")

def rsa_encrypt_no_padding(plaintext, pub_key_b64):
    """RSAEncrypt - RSA/ECB/NoPadding (raw: m^e mod n)"""
    pub = load_der_public_key(base64.b64decode(pub_key_b64), backend=default_backend())
    nums = pub.public_numbers()
    n, e = nums.n, nums.e
    key_size = (n.bit_length() + 7) // 8
    m = int.from_bytes(plaintext.encode("utf-8").rjust(key_size, b'\x00'), 'big')
    c = pow(m, e, n)
    return base64.b64encode(c.to_bytes(key_size, 'big')).decode("utf-8").replace("\n","").replace("\r","")

class DhAdapter(HTTPAdapter):
    def init_poolmanager(self, *args, **kwargs):
        ctx = create_urllib3_context()
        ctx.set_ciphers("DEFAULT:@SECLEVEL=1")
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        kwargs["ssl_context"] = ctx
        return super().init_poolmanager(*args, **kwargs)

myvnpt_sess = requests.Session()
myvnpt_sess.mount("https://", DhAdapter())

# ===========================================================================
# ADB HELPERS
# ===========================================================================
def adb_shell(cmd):
    r = subprocess.run([ADB, "shell", "su", "-c", cmd],
                       capture_output=True, text=True, timeout=15)
    return r.stdout.strip()

def parse_pref(key, xml):
    m = re.search(r'name="' + re.escape(key) + r'"[^>]*>([^<]+)<', xml)
    return m.group(1).strip() if m else ""

# ===========================================================================
# STEP 1: Read SharedPrefs
# ===========================================================================
SEP = "=" * 70
print(SEP)
print("  eKYC Full Flow - My VNPT")
print(SEP)
print("\n[STEP 1] Reading SharedPrefs via ADB...")

xml = adb_shell("cat /data/data/com.vnp.myvinaphone/shared_prefs/com.vnp.myvinaphone.xml")

encrypted_priv  = parse_pref("rsa_key_private", xml)
server_pub_enc  = parse_pref("server_public_key", xml)
SESSION_ID      = parse_pref("login_session", xml) or parse_pref("API_SESSION_ID", xml)
MSISDN          = parse_pref("phone_number_login", xml) or "84855998879"

print(f"  SESSION: {SESSION_ID[:38]}...")
print(f"  MSISDN:  {MSISDN}")

if not encrypted_priv:
    print("  ERROR: Cannot read rsa_key_private. Check ADB/root.")
    sys.exit(1)

RSA_PRIVATE_KEY = aes_decrypt(encrypted_priv)
SERVER_PUBLIC_KEY = aes_decrypt(server_pub_enc) if server_pub_enc else \
    "MFwwDQYJKoZIhvcNAQEBBQADSwAwSAJBAJVQmBmaju4sA19woqsYnM2rhR5aNp6CzSsPSaIgOv+NlN6mLqITNr0hlRbYvDRQ6KEs2V1j1wtnrzTzXUwfM3cCAwEAAQ=="
print(f"  Private Key: OK ({len(RSA_PRIVATE_KEY)} chars)")
print(f"  Server Pub Key: OK ({len(SERVER_PUBLIC_KEY)} chars)")

# ===========================================================================
# STEP 2: Generate AES_KEY_GENERATOR
# ===========================================================================
CHARSET = string.ascii_letters + string.digits
AES_SESSION_KEY = "".join(secrets.choice(CHARSET) for _ in range(32))
print(f"\n[STEP 2] AES_KEY_GENERATOR: {AES_SESSION_KEY}")

# ===========================================================================
# STEP 3: Build getChallengeCodeSdkEkyc request body
# secureCode = SHA256(MSISDN + "|" + MSISDN + "|" + SECRET_AUTHEN)
# ===========================================================================
REQUEST_ID  = str(uuid.uuid4())
SECURE_CODE = hashlib.sha256(f"{MSISDN}|{MSISDN}|{SECRET_AUTHEN}".encode()).hexdigest()

body = OrderedDict([
    ("msisdn",           MSISDN),
    ("msisdnChangeImei", MSISDN),
    ("requestId",        REQUEST_ID),
    ("secureCode",       SECURE_CODE),
    ("session",          SESSION_ID),
])
body_json    = json.dumps(body, separators=(",", ":"))
REQUEST_DATA = aes_encrypt(body_json, AES_SESSION_KEY)

print(f"\n[STEP 3] Request body built:")
print(f"  secureCode: {SECURE_CODE}")
print(f"  requestData: {REQUEST_DATA[:60]}...")

# ===========================================================================
# STEP 4: Sign + Encrypt for X-Signature / X-Secret-Key
# ===========================================================================
print("\n[STEP 4] Computing X-Signature + X-Secret-Key...")
X_SIGNATURE  = sign_sha256_rsa(REQUEST_DATA, RSA_PRIVATE_KEY)
X_SECRET_KEY = rsa_encrypt_no_padding(AES_SESSION_KEY, SERVER_PUBLIC_KEY)
print(f"  X-Signature:  {X_SIGNATURE[:50]}...")
print(f"  X-Secret-Key: {X_SECRET_KEY[:50]}...")

# ===========================================================================
# STEP 5: Call getChallengeCodeSdkEkyc
# ===========================================================================
API_URL = f"{MYVNPT_BASE}/tel_service/active_sim/v2/getChallengeCodeSdkEkyc"

headers = {
    "Content-Type":  "application/json; charset=UTF-8",
    "Cache-Control": "no-cache",
    "Channel-Code":  "APP",
    "Device-Info":   DEVICE_INFO,
    "Language":      "vi_VN",
    "Authorization": BEARER_TOKEN,
    "X-Secret-Key":  X_SECRET_KEY,
    "X-Signature":   X_SIGNATURE,
    "partnerCode":   "MYVNPT",
    "User-Agent":    "okhttp/4.7.2",
}
payload = json.dumps({"requestData": REQUEST_DATA}, separators=(",", ":"))

print(f"\n[STEP 5] Calling getChallengeCodeSdkEkyc...")

resp = myvnpt_sess.post(API_URL, headers=headers, data=payload, timeout=30, verify=False)
print(f"  HTTP: {resp.status_code}")

resp_json  = resp.json()
error_code = resp_json.get("errorCode")
if error_code and error_code not in ("0", "00"):
    print(f"  FAILED: errorCode={error_code} | {resp_json.get('message')}")
    sys.exit(1)

print(f"  SUCCESS! errorCode={error_code or 'none'}")
ENC_DATA = resp_json.get("data", "")

# ===========================================================================
# STEP 6: Decrypt response -> challengeCode + ekycToken
# ===========================================================================
print("\n[STEP 6] Decrypting response...")
plaintext = aes_decrypt_gen(ENC_DATA, AES_SESSION_KEY)
result    = json.loads(plaintext)

CHALLENGE_CODE_HEX = result.get("challengeCode", "")
ekyc_token_raw     = result.get("ekycToken", {}) or {}

print(f"  errorCode:    {result.get('errorCode')}")
print(f"  message:      {result.get('message')}")
print(f"  challengeCode (first 60): {CHALLENGE_CODE_HEX[:60]}...")
print(f"  ekycExpire:   {result.get('ekycExpireDate')} ({result.get('ekycExpireTimeDisplay')})")

# Decrypt ekycToken fields with MASTER key
print("\n[STEP 6b] Decrypting ekycToken...")
ACCESS_TOKEN = aes_decrypt(ekyc_token_raw.get("access_token", ""))
TOKEN_ID     = aes_decrypt(ekyc_token_raw.get("token_id", ""))
TOKEN_KEY    = aes_decrypt(ekyc_token_raw.get("token_tokenkey", ""))
print(f"  Token-id:  {TOKEN_ID}")
print(f"  Token-key: {TOKEN_KEY[:50]}...")
print(f"  JWT bearer (first 50): {ACCESS_TOKEN[:50]}...")

# ===========================================================================
# STEP 7: Upload face images to VNPT IDG file service
# ===========================================================================
print("\n" + SEP)
print("  STEP 7: Face liveness detection (VNPT IDG API)")
print(SEP)
print("\n  Provide 2 selfie photos (or pass as args: python ekyc_full.py near.jpg far.jpg)")
if len(sys.argv) >= 3:
    NEAR_IMG = sys.argv[1].strip('"')
    FAR_IMG  = sys.argv[2].strip('"')
    print(f"  NEAR: {NEAR_IMG}")
    print(f"  FAR:  {FAR_IMG}")
else:
    NEAR_IMG = input("  Path to NEAR face image (close-up, .jpg): ").strip().strip('"')
    FAR_IMG  = input("  Path to FAR face image (slightly farther, .jpg): ").strip().strip('"')

for p in [NEAR_IMG, FAR_IMG]:
    if not os.path.exists(p):
        print(f"  ERROR: File not found: {p}")
        sys.exit(1)

def idg_headers(json_body=False):
    h = {
        "Authorization": f"bearer {ACCESS_TOKEN}",
        "Token-id":      TOKEN_ID,
        "Token-key":     TOKEN_KEY,
        "mac-address":   MAC_ADDRESS,
    }
    if json_body:
        h["Content-Type"] = "application/json"
    return h

def upload_image(path, name, title="ocr front", desc="ocr front old type"):
    """Upload image to IDG file service, returns hash path"""
    url = f"{IDG_BASE}/file-service/v1/addFile"
    with open(path, "rb") as f:
        data = f.read()
    # Determine content type
    ext = os.path.splitext(path)[1].lower()
    ct  = "image/png" if ext == ".png" else "image/jpeg"
    print(f"    Size: {len(data)//1024} KB, type: {ct}")
    try:
        r = requests.post(
            url,
            headers=idg_headers(),
            timeout=60,
            verify=False,
            files  ={"file": (name, data, ct)},
            data   ={"title": title, "description": desc},  # required by IDG API
        )
        print(f"  Upload {name}: HTTP {r.status_code}")
        rj = r.json()
        msg = rj.get("message", "")
        obj = rj.get("object", {})
        h   = obj.get("hash", "")
        if rj.get("error"):
            print(f"    ERROR: {rj.get('error')}")
        else:
            print(f"    message: {msg} | hash: {h[:60]}")
        return h
    except Exception as e:
        print(f"  Upload ERROR: {e}")
        return ""

print("\n  Uploading images to api.idg.vnpt.vn...")
NEAR_HASH = upload_image(NEAR_IMG, "near_portrait_preview")
FAR_HASH  = upload_image(FAR_IMG,  "far_portrait_preview")

if not NEAR_HASH or not FAR_HASH:
    print("\n  ERROR: Image upload failed. Check JWT / Token-id validity.")
    sys.exit(1)

# ===========================================================================
# STEP 8: Face mask detection
# POST /ai/v4/face/mask?challenge_code={hex}
# ===========================================================================
print("\n[STEP 8] Face mask detection...")
mask_url  = f"{IDG_BASE}/ai/v4/face/mask?challenge_code={CHALLENGE_CODE_HEX}"
mask_body = {
    "img":            FAR_HASH,
    "client_session": CLIENT_SESSION,
    "token":          ACCESS_TOKEN,
    "step_id":        0,
}
try:
    r = requests.post(mask_url, headers=idg_headers(True), json=mask_body,
                      timeout=30, verify=False)
    print(f"  HTTP: {r.status_code}")
    if r.status_code != 200:
        print(f"  ERROR response: {r.text[:500]}")
    mask_rj        = r.json()
    MASK_DATA_B64  = mask_rj.get("dataBase64", "")
    MASK_DATA_SIGN = mask_rj.get("dataSign", "")
    print(f"  dataSign: {MASK_DATA_SIGN[:50]}..." if MASK_DATA_SIGN else "  No dataSign")
    if not MASK_DATA_SIGN:
        print(f"  Full response: {json.dumps(mask_rj)[:400]}")
except Exception as e:
    print(f"  Face mask ERROR: {e}")
    MASK_DATA_B64 = MASK_DATA_SIGN = ""

# ===========================================================================
# STEP 9: Liveness 3D detection
# POST /ai/v4/face/liveness-3d?challenge_code={hex}
# ===========================================================================
print("\n[STEP 9] Liveness 3D detection...")
live_url  = f"{IDG_BASE}/ai/v4/face/liveness-3d?challenge_code={CHALLENGE_CODE_HEX}"
live_body = {
    "far_img":        FAR_HASH,
    "near_img":       NEAR_HASH,
    "scan3d":         FAR_HASH,   # fallback (3D scan not available from photo)
    "client_session": CLIENT_SESSION,
    "token":          ACCESS_TOKEN,
}
try:
    r = requests.post(live_url, headers=idg_headers(True), json=live_body,
                      timeout=30, verify=False)
    print(f"  HTTP: {r.status_code}")
    if r.status_code != 200:
        print(f"  ERROR response: {r.text[:500]}")
    live_rj        = r.json()
    LIVE_DATA_B64  = live_rj.get("dataBase64", "")
    LIVE_DATA_SIGN = live_rj.get("dataSign", "")
    print(f"  dataSign: {LIVE_DATA_SIGN[:50]}..." if LIVE_DATA_SIGN else "  No dataSign")

    # Decode to show liveness result
    if LIVE_DATA_B64:
        try:
            live_plain = json.loads(base64.b64decode(LIVE_DATA_B64 + "=="))
            obj        = live_plain.get("object", {})
            print(f"\n  === Liveness result ===")
            print(f"  liveness:      {obj.get('liveness')}")
            print(f"  liveness_msg:  {obj.get('liveness_msg')}")
            print(f"  liveness_prob: {obj.get('liveness_prob')}")
            print(f"  gender:        {obj.get('gender')}")
            print(f"  age:           {obj.get('age')}")
        except Exception:
            print(f"  Full response: {json.dumps(live_rj)[:300]}")
    if not LIVE_DATA_SIGN:
        print(f"  Full response: {json.dumps(live_rj)[:300]}")
except Exception as e:
    print(f"  Liveness ERROR: {e}")
    LIVE_DATA_B64 = LIVE_DATA_SIGN = ""

# ===========================================================================
# STEP 10: Save all results
# ===========================================================================
log_id = f"{TOKEN_ID}-Zuulserver"
results = {
    # For saveLogEkyc
    "challengeCode":      CHALLENGE_CODE_HEX,
    "session":            SESSION_ID,
    "msisdn":             MSISDN,
    "requestId":          REQUEST_ID,
    # Liveness data
    "liveNessFace":       LIVE_DATA_B64,
    "liveNessFaceSign":   LIVE_DATA_SIGN,
    "liveNessFaceLogId":  log_id,
    # Masked face
    "maskedFace":         MASK_DATA_B64,
    "maskedFaceSign":     MASK_DATA_SIGN,
    "maskedFaceLogId":    log_id,
    # Image hashes
    "nearHash":           NEAR_HASH,
    "farHash":            FAR_HASH,
    # eKYC metadata
    "ekycToken_access":   ACCESS_TOKEN,
    "ekycToken_id":       TOKEN_ID,
    "ekycExpire":         result.get("ekycExpireDate"),
}
with open("ekyc_result.json", "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2, ensure_ascii=False)

print("\n" + SEP)
liveness_ok = bool(LIVE_DATA_SIGN)
mask_ok     = bool(MASK_DATA_SIGN)
print(f"  DONE!")
print(f"  Face mask:  {'OK' if mask_ok else 'FAILED'}")
print(f"  Liveness:   {'OK' if liveness_ok else 'FAILED'}")
print(f"  Saved to:   ekyc_result.json")
print(SEP)
