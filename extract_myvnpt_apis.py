from __future__ import annotations

import html
import re
import subprocess
from datetime import datetime
from pathlib import Path
from urllib.parse import urlsplit


ROOT = Path(r"C:\Users\congn\Pictures\myvnpt")
JADX_DIR = ROOT / "MyVNPT_3.4.22_jadx"
BLUTTER_FILES = [
    ROOT / "MyVNPT_3.4.22_blutter" / "pp.txt",
    ROOT / "MyVNPT_3.4.22_blutter" / "objs.txt",
    ROOT / "MyVNPT_3.4.22_blutter" / "blutter_frida.js",
]
OUTPUT = ROOT / "MyVNPT_3.4.22_all_apis.txt"

TEXT_SUFFIXES = {
    ".java",
    ".kt",
    ".xml",
    ".json",
    ".html",
    ".htm",
    ".js",
    ".properties",
    ".txt",
    ".cfg",
    ".conf",
    ".yaml",
    ".yml",
}

URL_RE = re.compile(r"(?i)\b(?:https?|wss?)://[^\s<>\"']+")
QUOTED_PATH_RE = re.compile(
    r'''[\"'](/[-A-Za-z0-9._~{}@%:+]+(?:/[-A-Za-z0-9._~{}@%:+]+)+(?:\\?\?[-A-Za-z0-9._~{}@%:/?&=,+\\-]*)?)'''
)

API_MARKER_RE = re.compile(
    r"(?i)(?:"
    r"(?:^|[./_-])api(?:[./_-]|$)|"
    r"gateway|(?:^|[./_-])gw(?:[./_-]|$)|rest|graphql|mapi|"
    r"oauth|auth(?:entication)?|token|firebase|socket|"
    r"/v\d+(?:[./]\d+)*[/.:]|\.jsp(?:[/?]|$)|"
    r"/(?:services?|customer|payment|wallet|verify|identity|order|cert|csc|ssa|sso)/"
    r")"
)

API_ROOT_MARKERS = (
    "api",
    "service",
    "auth",
    "oauth",
    "token",
    "config",
    "verify",
    "order",
    "customer",
    "identity",
    "payment",
    "wallet",
    "firebase",
    "file",
    "gnms",
    "cert",
    "csc",
    "ssa",
    "sso",
    "thirdparty",
    "mapi",
    "account",
    "ai",
)

NON_API_ROOTS = {
    "android",
    "data",
    "dev",
    "proc",
    "sys",
    "system",
    "storage",
    "cache",
    "res",
    "assets",
    "meta-inf",
    "org",
    "javax",
    "kotlin",
    "lib",
    "fonts",
}


def text_files() -> list[Path]:
    files: list[Path] = []
    if JADX_DIR.exists():
        files.extend(
            path
            for path in JADX_DIR.rglob("*")
            if path.is_file() and path.suffix.lower() in TEXT_SUFFIXES
        )
    files.extend(path for path in BLUTTER_FILES if path.is_file())
    return files


def normalize_url(value: str) -> str:
    value = html.unescape(value)
    value = value.replace(r"\/", "/").replace(r"\u0026", "&")
    value = value.replace(r"\?", "?")
    value = re.split(r"\\[rnt]", value, maxsplit=1)[0]
    value = value.rstrip(".,;:!?)\\]}>")
    return value


def normalize_path(value: str) -> str:
    value = html.unescape(value).replace(r"\?", "?").replace(r"\u0026", "&")
    return value.rstrip(".,;:!?)\\]}>")


def looks_like_api_url(url: str) -> bool:
    try:
        parsed = urlsplit(url)
    except ValueError:
        return bool(API_MARKER_RE.search(url))
    host = parsed.netloc.lower()
    path = parsed.path.lower()
    if API_MARKER_RE.search(url):
        return True
    if any(part in host for part in ("vnpt", "vinaphone", "myvnpt", "vnp")):
        return bool(path and path != "/")
    return False


def looks_like_api_path(path: str) -> bool:
    clean = path.split("?", 1)[0]
    parts = [part for part in clean.lower().split("/") if part]
    if not parts or parts[0] in NON_API_ROOTS:
        return False
    root = parts[0]
    if root.startswith("v") and root[1:].replace(".", "").isdigit():
        return True
    if any(marker in root for marker in API_ROOT_MARKERS):
        return True
    if re.search(r"(?i)/(?:v\d+(?:\.\d+)*)/", clean):
        return True
    return any(
        re.search(
            r"(?i)^(?:get|set|list|create|update|delete|remove|add|check|confirm|register|"
            r"send|upload|download|search|query|fetch|request|init|login|logout|sign|pay)",
            part,
        )
        for part in parts[1:]
    )


def extract() -> tuple[set[str], set[str], int]:
    urls: set[str] = set()
    paths: set[str] = set()
    files = text_files()
    scanned = len(files)
    inputs = [str(JADX_DIR), *(str(path) for path in BLUTTER_FILES if path.is_file())]
    globs: list[str] = []
    for suffix in sorted(TEXT_SUFFIXES):
        globs.extend(["-g", f"*{suffix}"])

    url_result = subprocess.run(
        [
            "rg",
            "-o",
            "-I",
            "--no-filename",
            "--pcre2",
            *globs,
            r'''(?i)\b(?:https?|wss?)://[^\s<>\"']+''',
            *inputs,
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="ignore",
        check=False,
    )
    for raw in url_result.stdout.splitlines():
        value = normalize_url(raw)
        if len(value) > 10:
            urls.add(value)

    path_result = subprocess.run(
        [
            "rg",
            "-o",
            "-I",
            "--no-filename",
            "--pcre2",
            *globs,
            r'''[\"'](/[-A-Za-z0-9._~{}@%:+]+(?:/[-A-Za-z0-9._~{}@%:+]+)+(?:\\?\?[-A-Za-z0-9._~{}@%:/?&=,+\\-]*)?)''',
            *inputs,
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="ignore",
        check=False,
    )
    for raw in path_result.stdout.splitlines():
        value = normalize_path(raw[1:])
        if len(value) > 3:
            paths.add(value)
    return urls, paths, scanned


def write_section(handle, title: str, values: list[str]) -> None:
    handle.write(f"\n[{title}] ({len(values)})\n")
    handle.write("-" * 80 + "\n")
    for value in values:
        handle.write(value + "\n")


def main() -> None:
    urls, paths, scanned = extract()
    api_urls = sorted((url for url in urls if looks_like_api_url(url)), key=str.lower)
    other_urls = sorted((url for url in urls if url not in set(api_urls)), key=str.lower)
    api_paths = sorted((path for path in paths if looks_like_api_path(path)), key=str.lower)
    other_paths = sorted((path for path in paths if path not in set(api_paths)), key=str.lower)

    hosts = set()
    for url in urls:
        try:
            parsed = urlsplit(url)
        except ValueError:
            continue
        invalid_netloc = any(char in parsed.netloc for char in "%{}*\\")
        if parsed.scheme and parsed.netloc and not invalid_netloc and not parsed.netloc.startswith("."):
            hosts.add(f"{parsed.scheme.lower()}://{parsed.netloc.lower()}")

    with OUTPUT.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write("MY VNPT 3.4.22 - TONG HOP API / URL / ENDPOINT\n")
        handle.write("=" * 80 + "\n")
        handle.write(f"Generated: {datetime.now().astimezone().isoformat(timespec='seconds')}\n")
        handle.write(f"Scanned text files: {scanned}\n")
        handle.write(f"JADX source: {JADX_DIR}\n")
        handle.write(f"Blutter source: {BLUTTER_FILES[0].parent}\n")
        handle.write(
            "Note: Chuoi duoc trich xuat tinh; endpoint ghep dong luc chay co the chi hien "
            "thi theo tung phan. Muc OTHER duoc giu lai de doi chieu day du.\n"
        )

        write_section(handle, "HOSTS_AND_BASE_URLS", sorted(hosts, key=str.lower))
        write_section(handle, "LIKELY_ABSOLUTE_API_URLS", api_urls)
        write_section(handle, "LIKELY_RELATIVE_API_ENDPOINTS", api_paths)
        write_section(handle, "OTHER_EMBEDDED_ABSOLUTE_URLS", other_urls)
        write_section(handle, "OTHER_QUOTED_PATH_CANDIDATES", other_paths)

    print(f"Output: {OUTPUT}")
    print(f"Scanned files: {scanned}")
    print(f"Hosts/base URLs: {len(hosts)}")
    print(f"Likely absolute APIs: {len(api_urls)}")
    print(f"Likely relative APIs: {len(api_paths)}")
    print(f"Other absolute URLs: {len(other_urls)}")
    print(f"Other path candidates: {len(other_paths)}")


if __name__ == "__main__":
    main()
