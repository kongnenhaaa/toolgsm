#!/usr/bin/env python3
"""
Tool Fix GSM for Quectel EC20/EC2x-style modems.

The repair flow is intentionally narrow:
  * disable command echo and enable verbose modem errors;
  * route URCs to uart1;
  * disable IMS/UT so supplementary services use CS fallback;
  * leave the module's main IMS/MBN setting untouched;
  * close any stale USSD session and restore automatic network selection;
  * enable SIM hot-plug using the insert polarity reported by the modem;
  * reboot exactly once;
  * wait for the modem and report detailed SIM/network state;
  * if an ICCID is present, run one *101# lookup (with a no-DCS fallback).

ICCID is presence-only: it is never compared with an earlier value and never
used to block configuration repair. The tool never performs a factory/NV/IMEI
reset.
"""

from __future__ import annotations

import argparse
import os
import queue
import re
import sys
import threading
import time
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Callable, Iterable, Sequence

try:
    import serial
    from serial import SerialException
    from serial.tools import list_ports
except ImportError:
    serial = None
    SerialException = Exception
    list_ports = None


BAUD_RATE = 115_200
AT_TIMEOUT_SECONDS = 3.0
BOOT_INITIAL_DELAY_SECONDS = 8.0
BOOT_DEADLINE_SECONDS = 90.0
NETWORK_DEADLINE_SECONDS = 30.0

ECHO_DISABLE = "ATE0"
VERBOSE_ERRORS_ENABLE = "AT+CMEE=2"
UART1_QUERY = 'AT+QURCCFG="urcport"'
UART1_SET = 'AT+QURCCFG="urcport","uart1"'
IMS_QUERY = 'AT+QCFG="ims"'
IMS_UT_QUERY = 'AT+QCFG="ims/ut"'
IMS_UT_DISABLE = 'AT+QCFG="ims/ut",0'
NETWORK_MODE_QUERY = 'AT+QCFG="nwscanmode"'
NETWORK_MODE_AUTO = 'AT+QCFG="nwscanmode",0,0'
SIM_STATUS_QUERY = "AT+QSIMSTAT?"
SIM_STATUS_URC_ENABLE = "AT+QSIMSTAT=1"
SIM_DETECT_QUERY = "AT+QSIMDET?"
IDENTITY_QUERY = "ATI"
FIRMWARE_QUERY = "AT+CGMR"
NETWORK_INFO_QUERY = "AT+QNWINFO"
SERVING_CELL_QUERY = 'AT+QENG="servingcell"'
EXTENDED_ERROR_QUERY = "AT+CEER"
USSD_STATUS_QUERY = "AT+CUSD?"
REBOOT_COMMAND = "AT+CFUN=1,1"
ICCID_QUERY_COMMANDS = ("AT+ICCID", "AT+QCCID")
USSD_CANCEL = "AT+CUSD=2"
USSD_101_WITH_DCS = 'AT+CUSD=1,"*101#",15'
USSD_101_WITHOUT_DCS = 'AT+CUSD=1,"*101#"'
USSD_RESPONSE_TIMEOUT_SECONDS = 35.0

TERMINAL_RE = re.compile(
    r"(?:^|\r?\n)\s*(?:OK|ERROR|\+CME ERROR:[^\r\n]*|"
    r"\+CMS ERROR:[^\r\n]*)\s*(?:\r?\n|$)",
    re.IGNORECASE,
)
OK_RE = re.compile(r"(?:^|\r?\n)\s*OK\s*(?:\r?\n|$)", re.IGNORECASE)
ERROR_RE = re.compile(
    r"(?:^|\r?\n)\s*(?:ERROR|\+CME ERROR:[^\r\n]*|"
    r"\+CMS ERROR:[^\r\n]*)\s*(?:\r?\n|$)",
    re.IGNORECASE,
)


class RepairCancelled(RuntimeError):
    pass


class RepairError(RuntimeError):
    pass


@dataclass(frozen=True)
class AtResult:
    command: str
    response: str
    elapsed_seconds: float
    terminal_seen: bool

    @property
    def ok(self) -> bool:
        return bool(OK_RE.search(self.response)) and not ERROR_RE.search(self.response)

    @property
    def compact_response(self) -> str:
        compact = re.sub(r"\s+", " ", self.response).strip()
        return compact if compact else "(không có phản hồi)"


@dataclass
class RepairReport:
    port: str
    success: bool = False
    changed_commands: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    final_values: dict[str, str] = field(default_factory=dict)
    elapsed_seconds: float = 0.0
    error: str = ""

    @property
    def summary(self) -> str:
        if self.success and self.warnings:
            return "ĐÃ FIX - CÓ CẢNH BÁO"
        if self.success:
            return "ĐÃ FIX"
        return "FIX THẤT BẠI"


class TraceLogger:
    def __init__(
        self,
        output: Callable[[str], None] | None = None,
        log_path: Path | None = None,
    ) -> None:
        self._output = output or print
        self._lock = threading.Lock()
        self.log_path = log_path or default_log_path()
        self.log_path.parent.mkdir(parents=True, exist_ok=True)

    def write(self, port: str, message: str) -> None:
        timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
        line = f"[{timestamp}] [{port}] {message}"
        with self._lock:
            with self.log_path.open("a", encoding="utf-8") as stream:
                stream.write(line + "\n")
            self._output(line)


def default_log_path() -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    root = Path(local_app_data) if local_app_data else Path.home()
    return (
        root
        / "ToolGSM"
        / "HardwareFix"
        / "Logs"
        / f"gsm_fix_{datetime.now():%Y%m%d}.log"
    )


def require_pyserial() -> None:
    if serial is None or list_ports is None:
        raise RuntimeError(
            "Thiếu pyserial. Cài bằng lệnh: python -m pip install pyserial"
        )


def check_cancelled(cancel_event: threading.Event) -> None:
    if cancel_event.is_set():
        raise RepairCancelled("Đã dừng theo yêu cầu.")


def qcfg_first_value(response: str, key: str) -> int | None:
    match = re.search(
        rf'\+QCFG:\s*"{re.escape(key)}"\s*,\s*(-?\d+)',
        response,
        re.IGNORECASE,
    )
    return int(match.group(1)) if match else None


def cfun_value(response: str) -> int | None:
    match = re.search(r"\+CFUN:\s*(\d+)", response, re.IGNORECASE)
    return int(match.group(1)) if match else None


def cpin_value(response: str) -> str | None:
    match = re.search(r"\+CPIN:\s*([^\r\n]+)", response, re.IGNORECASE)
    return match.group(1).strip().upper() if match else None


def qsimdet_config(response: str) -> tuple[int, int] | None:
    match = re.search(
        r"\+QSIMDET:\s*([01])\s*,\s*([01])",
        response,
        re.IGNORECASE,
    )
    if not match:
        return None
    return int(match.group(1)), int(match.group(2))


def sim_hotplug_enable_commands(response: str) -> list[str]:
    config = qsimdet_config(response)
    if config is None:
        return []
    _enabled, polarity = config
    return [SIM_STATUS_URC_ENABLE, f"AT+QSIMDET=1,{polarity}"]


def iccid_value(response: str) -> str | None:
    match = re.search(r"(?<!\d)(89\d{16,20})(?!\d)", response)
    return match.group(1) if match else None


def registration_value(response: str, family: str) -> int | None:
    match = re.search(
        rf"\+{re.escape(family)}REG:\s*(\d+)(?:\s*,\s*(\d+))?",
        response,
        re.IGNORECASE,
    )
    if not match:
        return None
    return int(match.group(2) if match.group(2) is not None else match.group(1))


def uart1_is_active(response: str) -> bool:
    return bool(
        re.search(
            r'\+QURCCFG:\s*"urcport"\s*,\s*"uart1"',
            response,
            re.IGNORECASE,
        )
    )


def planned_config_commands(
    ims_response: str,
    ims_ut_response: str,
    network_mode_response: str,
) -> list[str]:
    # ims_response is collected for diagnostics only. Do not force the main
    # IMS setting to 1 or 2; Quectel documents ims/ut=0 as the CSFB control.
    _ = ims_response
    commands: list[str] = []
    if qcfg_first_value(ims_ut_response, "ims/ut") != 0:
        commands.append(IMS_UT_DISABLE)
    if qcfg_first_value(network_mode_response, "nwscanmode") != 0:
        commands.append(NETWORK_MODE_AUTO)
    return commands


def port_sort_key(name: str) -> tuple[int, str]:
    match = re.fullmatch(r"COM(\d+)", name.strip(), re.IGNORECASE)
    return (int(match.group(1)), name.upper()) if match else (sys.maxsize, name.upper())


def available_ports() -> list[tuple[str, str]]:
    require_pyserial()
    ports = [(item.device, item.description or "") for item in list_ports.comports()]
    return sorted(ports, key=lambda item: port_sort_key(item[0]))


class AtSerialSession:
    def __init__(
        self,
        port: str,
        logger: TraceLogger,
        cancel_event: threading.Event,
    ) -> None:
        self.port = port
        self.logger = logger
        self.cancel_event = cancel_event
        self._serial = None

    def __enter__(self) -> "AtSerialSession":
        self.open()
        return self

    def __exit__(self, _exc_type, _exc, _traceback) -> None:
        self.close()

    @property
    def is_open(self) -> bool:
        return bool(self._serial is not None and self._serial.is_open)

    def open(self) -> None:
        require_pyserial()
        check_cancelled(self.cancel_event)
        if self.is_open:
            return
        try:
            self._serial = serial.Serial(
                port=self.port,
                baudrate=BAUD_RATE,
                bytesize=serial.EIGHTBITS,
                parity=serial.PARITY_NONE,
                stopbits=serial.STOPBITS_ONE,
                timeout=0,
                write_timeout=2,
                rtscts=False,
                dsrdtr=False,
            )
            self._serial.dtr = True
            self._serial.rts = True
            time.sleep(0.2)
            self._drain_input()
            self.logger.write(self.port, "Đã mở cổng.")
        except Exception:
            self.close()
            raise

    def close(self) -> None:
        current = self._serial
        self._serial = None
        if current is None:
            return
        try:
            if current.is_open:
                current.close()
        except Exception:
            pass

    def _drain_input(self) -> None:
        if not self.is_open:
            return
        try:
            self._serial.reset_input_buffer()
        except Exception:
            try:
                _ = self._serial.read(self._serial.in_waiting or 1)
            except Exception:
                pass

    def send(
        self,
        command: str,
        timeout_seconds: float = AT_TIMEOUT_SECONDS,
    ) -> AtResult:
        check_cancelled(self.cancel_event)
        if not self.is_open:
            raise RepairError(f"{self.port} chưa được mở.")

        self._drain_input()
        payload = (command + "\r").encode("ascii")
        started = time.monotonic()
        self.logger.write(self.port, f"TX  {command}")
        try:
            self._serial.write(payload)
            self._serial.flush()
        except Exception as exc:
            raise RepairError(f"Không ghi được {command}: {exc}") from exc

        chunks: list[bytes] = []
        terminal_seen = False
        while time.monotonic() - started < timeout_seconds:
            check_cancelled(self.cancel_event)
            try:
                waiting = self._serial.in_waiting
                if waiting:
                    chunks.append(self._serial.read(waiting))
            except Exception as exc:
                raise RepairError(f"Mất kết nối khi chờ {command}: {exc}") from exc

            response = b"".join(chunks).decode("utf-8", errors="replace").replace(
                "\x00", ""
            )
            if TERMINAL_RE.search(response):
                terminal_seen = True
                break
            time.sleep(0.03)

        elapsed = time.monotonic() - started
        response = b"".join(chunks).decode("utf-8", errors="replace").replace(
            "\x00", ""
        )
        result = AtResult(command, response.strip(), elapsed, terminal_seen)
        self.logger.write(
            self.port,
            f"RX  {command} -> {result.compact_response} ({elapsed:.2f}s)",
        )
        return result

    def require_ok(
        self,
        command: str,
        timeout_seconds: float = AT_TIMEOUT_SECONDS,
    ) -> AtResult:
        result = self.send(command, timeout_seconds)
        if not result.ok:
            raise RepairError(
                f"{command} không trả OK: {result.compact_response}"
            )
        return result

    def submit_reboot_once(self) -> AtResult:
        # This method is the only place allowed to send a modem reboot. The
        # command is written exactly once and is never retried.
        check_cancelled(self.cancel_event)
        if not self.is_open:
            raise RepairError(f"{self.port} chưa được mở để reboot.")

        self._drain_input()
        started = time.monotonic()
        self.logger.write(self.port, f"TX  {REBOOT_COMMAND} (DUY NHẤT 1 LẦN)")
        try:
            self._serial.write((REBOOT_COMMAND + "\r").encode("ascii"))
            self._serial.flush()
        except Exception as exc:
            raise RepairError(f"Không gửi được lệnh reboot: {exc}") from exc

        chunks: list[bytes] = []
        while time.monotonic() - started < 3.0:
            check_cancelled(self.cancel_event)
            try:
                waiting = self._serial.in_waiting
                if waiting:
                    chunks.append(self._serial.read(waiting))
            except Exception:
                # The UART can disappear immediately after accepting reboot.
                break
            response = b"".join(chunks).decode("utf-8", errors="replace")
            if TERMINAL_RE.search(response):
                break
            time.sleep(0.03)

        response = (
            b"".join(chunks)
            .decode("utf-8", errors="replace")
            .replace("\x00", "")
            .strip()
        )
        result = AtResult(
            REBOOT_COMMAND,
            response,
            time.monotonic() - started,
            bool(TERMINAL_RE.search(response)),
        )
        if ERROR_RE.search(response):
            raise RepairError(
                f"Modem từ chối reboot: {result.compact_response}"
            )
        self.logger.write(
            self.port,
            f"RX  {REBOOT_COMMAND} -> {result.compact_response}",
        )
        return result

    def run_ussd_request(
        self,
        command: str,
        timeout_seconds: float = USSD_RESPONSE_TIMEOUT_SECONDS,
    ) -> AtResult:
        """Send CUSD=1 and wait past the immediate OK for +CUSD/error."""
        check_cancelled(self.cancel_event)
        if not self.is_open:
            raise RepairError(f"{self.port} chưa được mở để dò *101#.")

        self._drain_input()
        started = time.monotonic()
        self.logger.write(self.port, f"TX  {command}")
        try:
            self._serial.write((command + "\r").encode("ascii"))
            self._serial.flush()
        except Exception as exc:
            raise RepairError(f"Không gửi được {command}: {exc}") from exc

        chunks: list[bytes] = []
        completed = False
        while time.monotonic() - started < timeout_seconds:
            check_cancelled(self.cancel_event)
            try:
                waiting = self._serial.in_waiting
                if waiting:
                    chunks.append(self._serial.read(waiting))
            except Exception as exc:
                raise RepairError(
                    f"Mất kết nối khi chờ kết quả *101#: {exc}"
                ) from exc

            response = (
                b"".join(chunks)
                .decode("utf-8", errors="replace")
                .replace("\x00", "")
            )
            if re.search(r"(?:^|\r?\n)\s*\+CUSD:", response, re.IGNORECASE):
                completed = True
                break
            if ERROR_RE.search(response):
                completed = True
                break
            time.sleep(0.05)

        elapsed = time.monotonic() - started
        response = (
            b"".join(chunks)
            .decode("utf-8", errors="replace")
            .replace("\x00", "")
            .strip()
        )
        result = AtResult(command, response, elapsed, completed)
        self.logger.write(
            self.port,
            f"RX  {command} -> {result.compact_response} ({elapsed:.2f}s)",
        )
        return result

    def reconnect_after_reboot(self) -> None:
        self.close()
        self.logger.write(
            self.port,
            f"Chờ modem khởi động {BOOT_INITIAL_DELAY_SECONDS:.0f} giây...",
        )
        wait_with_cancel(BOOT_INITIAL_DELAY_SECONDS, self.cancel_event)
        deadline = time.monotonic() + BOOT_DEADLINE_SECONDS
        attempt = 0
        last_error = ""

        while time.monotonic() < deadline:
            check_cancelled(self.cancel_event)
            attempt += 1
            try:
                self.open()
                probe = self.send("AT", timeout_seconds=1.5)
                if probe.ok:
                    self.logger.write(
                        self.port,
                        f"Modem đã lên lại sau {attempt} lần dò.",
                    )
                    return
                last_error = probe.compact_response
            except RepairCancelled:
                raise
            except Exception as exc:
                last_error = str(exc)
            self.close()
            wait_with_cancel(1.0, self.cancel_event)

        raise RepairError(
            f"Modem không trả AT sau reboot trong {BOOT_DEADLINE_SECONDS:.0f}s"
            + (f": {last_error}" if last_error else ".")
        )


def wait_with_cancel(seconds: float, cancel_event: threading.Event) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        check_cancelled(cancel_event)
        time.sleep(min(0.1, max(0.0, deadline - time.monotonic())))


def query_network_state(
    session: AtSerialSession,
) -> tuple[dict[str, str], bool]:
    final: dict[str, str] = {}
    registered = False
    for family in ("C", "CG", "CE"):
        result = session.send(f"AT+{family}REG?")
        final[f"{family}REG"] = result.compact_response
        status = registration_value(result.response, family)
        if status in (1, 5):
            registered = True

    cops = session.send("AT+COPS?", timeout_seconds=5.0)
    csq = session.send("AT+CSQ")
    final["COPS"] = cops.compact_response
    final["CSQ"] = csq.compact_response
    return final, registered


def record_optional_query(
    session: AtSerialSession,
    report: RepairReport,
    key: str,
    command: str,
    timeout_seconds: float = AT_TIMEOUT_SECONDS,
) -> AtResult:
    result = session.send(command, timeout_seconds=timeout_seconds)
    report.final_values[key] = result.compact_response
    if not result.ok:
        session.logger.write(
            session.port,
            f"Chẩn đoán {command} không được firmware hỗ trợ hoặc chưa sẵn sàng; tiếp tục.",
        )
    return result


def run_nonfatal_command(
    session: AtSerialSession,
    report: RepairReport,
    key: str,
    command: str,
) -> AtResult:
    result = session.send(command)
    report.final_values[key] = result.compact_response
    if not result.ok:
        message = f"{command} chưa trả OK; đã tiếp tục mà không reset thêm"
        report.warnings.append(message)
        session.logger.write(session.port, f"CẢNH BÁO: {message}.")
    return result


def find_present_iccid(session: AtSerialSession) -> tuple[str | None, str]:
    last_response = ""
    for command in ICCID_QUERY_COMMANDS:
        result = session.send(command)
        last_response = result.compact_response
        value = iccid_value(result.response)
        if value:
            session.logger.write(
                session.port,
                f"Đã phát hiện ICCID {value}; chỉ dùng để quyết định dò *101#.",
            )
            return value, result.compact_response
    session.logger.write(
        session.port,
        "Không tìm thấy ICCID; bỏ qua *101#.",
    )
    return None, last_response


def run_automatic_101(session: AtSerialSession) -> AtResult:
    last_result = AtResult(
        USSD_101_WITH_DCS,
        "ERROR: chưa chạy *101#",
        0.0,
        False,
    )
    for attempt, command in enumerate(
        (USSD_101_WITH_DCS, USSD_101_WITHOUT_DCS),
        start=1,
    ):
        cleanup = session.send(USSD_CANCEL)
        if not cleanup.ok:
            session.logger.write(
                session.port,
                f"CẢNH BÁO: {USSD_CANCEL} chưa trả OK; vẫn thử *101#.",
            )
        wait_with_cancel(1.0, session.cancel_event)
        last_result = session.run_ussd_request(command)
        if re.search(
            r"(?:^|\r?\n)\s*\+CUSD:",
            last_result.response,
            re.IGNORECASE,
        ):
            session.logger.write(
                session.port,
                f"*101# thành công ở lần {attempt}.",
            )
            return last_result
        if attempt == 1:
            session.logger.write(
                session.port,
                "*101# bản DCS=15 chưa có +CUSD; thử lại không DCS.",
            )
    return last_result


def repair_port(
    port: str,
    logger: TraceLogger,
    cancel_event: threading.Event | None = None,
) -> RepairReport:
    cancel_event = cancel_event or threading.Event()
    started = time.monotonic()
    report = RepairReport(port=port)
    logger.write(
        port,
        "BẮT ĐẦU FIX (ICCID chỉ kiểm tra hiện diện; có thì dò *101#).",
    )

    try:
        with AtSerialSession(port, logger, cancel_event) as session:
            at_result = session.require_ok("AT")
            report.final_values["AT"] = at_result.compact_response
            session.require_ok(ECHO_DISABLE)
            session.require_ok(VERBOSE_ERRORS_ENABLE)

            # Put URCs on the physical UART before doing anything disruptive.
            session.require_ok(UART1_SET)

            record_optional_query(
                session,
                report,
                "IDENTITY",
                IDENTITY_QUERY,
            )
            record_optional_query(
                session,
                report,
                "FIRMWARE",
                FIRMWARE_QUERY,
            )
            record_optional_query(
                session,
                report,
                "QSIMSTAT_BEFORE",
                SIM_STATUS_QUERY,
            )
            sim_detect_before = record_optional_query(
                session,
                report,
                "QSIMDET_BEFORE",
                SIM_DETECT_QUERY,
            )
            record_optional_query(
                session,
                report,
                "CUSD_BEFORE",
                USSD_STATUS_QUERY,
            )

            ims = session.send(IMS_QUERY)
            ims_ut = session.send(IMS_UT_QUERY)
            network_mode = session.send(NETWORK_MODE_QUERY)
            cfun = session.send("AT+CFUN?")
            cpin = session.send("AT+CPIN?")
            report.final_values["CPIN_TRƯỚC"] = cpin.compact_response
            report.final_values["CFUN_TRƯỚC"] = cfun.compact_response

            # Close a stale supplementary-service session before changing
            # configuration. Some firmware returns ERROR when no session
            # exists, so this cleanup must never abort the repair.
            run_nonfatal_command(
                session,
                report,
                "CUSD_CLEANUP_BEFORE_REBOOT",
                USSD_CANCEL,
            )

            current_hotplug = qsimdet_config(sim_detect_before.response)
            hotplug_commands = sim_hotplug_enable_commands(
                sim_detect_before.response
            )
            if hotplug_commands:
                # QSIMDET reports the board's configured insert level even when
                # detection is disabled. Reuse that level; do not guess or flip
                # polarity. The existing single reboot makes the change active.
                expected_hotplug = (1, current_hotplug[1])
                for index, command in enumerate(hotplug_commands, start=1):
                    run_nonfatal_command(
                        session,
                        report,
                        f"HOTPLUG_ENABLE_{index}",
                        command,
                    )
                action = "PRESERVED" if current_hotplug[0] == 1 else "ENABLED"
                report.final_values["SIM_HOTPLUG"] = (
                    f"{action}_{expected_hotplug[0]}_{expected_hotplug[1]}"
                )
            else:
                expected_hotplug = None
                reason = "SKIPPED_UNSUPPORTED_OR_MALFORMED"
                report.final_values["SIM_HOTPLUG"] = reason
                logger.write(
                    port,
                    "Firmware không trả về cấu hình QSIMDET hợp lệ; "
                    "bỏ qua hot-plug, không đoán polarity.",
                )

            for command in planned_config_commands(
                ims.response,
                ims_ut.response,
                network_mode.response,
            ):
                session.require_ok(command)
                report.changed_commands.append(command)

            # Re-assert uart1 in case the modem changed URC routing previously.
            session.require_ok(UART1_SET)

            # A manual Fix action always performs one bounded reboot. It never
            # loops and never sends the command again if reconnection is slow.
            session.submit_reboot_once()
            session.reconnect_after_reboot()

            session.require_ok(ECHO_DISABLE)
            session.require_ok(VERBOSE_ERRORS_ENABLE)
            session.require_ok(UART1_SET)
            uart = session.require_ok(UART1_QUERY)
            ims_after = session.require_ok(IMS_QUERY)
            ims_ut_after = session.require_ok(IMS_UT_QUERY)
            network_mode_after = session.require_ok(NETWORK_MODE_QUERY)
            cfun_after = session.require_ok("AT+CFUN?")
            # SIM state is informational only. A missing/locked SIM must not
            # invalidate an otherwise successful modem configuration repair.
            cpin_after = session.send("AT+CPIN?")
            record_optional_query(
                session,
                report,
                "QSIMSTAT_AFTER",
                SIM_STATUS_QUERY,
            )
            sim_detect_after = record_optional_query(
                session,
                report,
                "QSIMDET_AFTER",
                SIM_DETECT_QUERY,
            )
            record_optional_query(
                session,
                report,
                "CUSD_AFTER_REBOOT",
                USSD_STATUS_QUERY,
            )

            report.final_values["URC"] = uart.compact_response
            report.final_values["IMS"] = ims_after.compact_response
            report.final_values["IMS_UT"] = ims_ut_after.compact_response
            report.final_values["NETWORK_MODE"] = (
                network_mode_after.compact_response
            )
            report.final_values["CFUN"] = cfun_after.compact_response
            report.final_values["CPIN"] = cpin_after.compact_response

            verification_errors: list[str] = []
            if not uart1_is_active(uart.response):
                verification_errors.append("URC chưa ở uart1")
            if qcfg_first_value(ims_ut_after.response, "ims/ut") != 0:
                verification_errors.append("IMS/UT chưa tắt (0)")
            if qcfg_first_value(network_mode_after.response, "nwscanmode") != 0:
                verification_errors.append("chế độ mạng chưa AUTO (0)")
            if cfun_value(cfun_after.response) != 1:
                verification_errors.append("CFUN chưa về 1")

            if verification_errors:
                raise RepairError("; ".join(verification_errors))

            if (
                expected_hotplug is not None
                and qsimdet_config(sim_detect_after.response) != expected_hotplug
            ):
                report.warnings.append(
                    "QSIMDET sau reboot không còn đúng cấu hình đã đọc trước đó; "
                    "không thử polarity khác và không reboot lại"
                )

            pin_state = cpin_value(cpin_after.response)
            if pin_state != "READY":
                report.warnings.append(
                    f"SIM chưa READY sau fix ({pin_state or 'không đọc được CPIN'})"
                )

            network_deadline = time.monotonic() + NETWORK_DEADLINE_SECONDS
            network_values: dict[str, str] = {}
            network_registered = False
            while time.monotonic() < network_deadline:
                check_cancelled(cancel_event)
                network_values, network_registered = query_network_state(session)
                if network_registered:
                    break
                logger.write(port, "Đang chờ modem đăng ký mạng...")
                wait_with_cancel(2.0, cancel_event)
            report.final_values.update(network_values)
            if not network_registered:
                report.warnings.append(
                    "Chưa đăng ký mạng; kiểm tra SIM, anten, nguồn hoặc nhà mạng"
                )
                record_optional_query(
                    session,
                    report,
                    "CEER_NETWORK_FAILURE",
                    EXTENDED_ERROR_QUERY,
                )

            record_optional_query(
                session,
                report,
                "QNWINFO",
                NETWORK_INFO_QUERY,
                timeout_seconds=5.0,
            )
            record_optional_query(
                session,
                report,
                "SERVING_CELL",
                SERVING_CELL_QUERY,
                timeout_seconds=5.0,
            )

            # Latest requirement: ICCID is a presence gate only. There is no
            # before/after comparison and a missing ICCID never triggers
            # another reboot.
            detected_iccid, iccid_response = find_present_iccid(session)
            report.final_values["ICCID"] = iccid_response
            if detected_iccid:
                ussd_result = run_automatic_101(session)
                report.final_values["USSD_101"] = ussd_result.compact_response
                if not re.search(
                    r"(?:^|\r?\n)\s*\+CUSD:",
                    ussd_result.response,
                    re.IGNORECASE,
                ):
                    record_optional_query(
                        session,
                        report,
                        "CEER_USSD_FAILURE",
                        EXTENDED_ERROR_QUERY,
                    )
                    report.warnings.append(
                        "*101# chưa trả +CUSD sau khi fix; không reboot lại"
                    )
            else:
                report.final_values["USSD_101"] = "SKIPPED_NO_ICCID"

            report.success = True
            logger.write(
                port,
                "HOÀN TẤT: ATE0 + CMEE=2 + uart1 + IMS/UT=0 + mạng AUTO; "
                "đã reboot đúng 1 lần; QSIMDET không bị đoán polarity; "
                "ICCID có thì đã dò *101#.",
            )
    except RepairCancelled as exc:
        report.error = str(exc)
        logger.write(port, f"ĐÃ DỪNG: {exc}")
    except Exception as exc:
        report.error = str(exc)
        logger.write(port, f"THẤT BẠI: {exc}")
    finally:
        report.elapsed_seconds = time.monotonic() - started
        logger.write(
            port,
            f"KẾT QUẢ: {report.summary}; {report.elapsed_seconds:.1f}s.",
        )

    return report


def run_self_test() -> None:
    assert TERMINAL_RE.search("\r\nOK\r\n")
    assert TERMINAL_RE.search("\r\n+CME ERROR: 100\r\n")
    assert not TERMINAL_RE.search("+CPIN: READY")
    assert AtResult("AT", "\r\nOK\r\n", 0.1, True).ok
    assert not AtResult("AT", "\r\nERROR\r\n", 0.1, True).ok

    assert qcfg_first_value('+QCFG: "ims",1,0\r\nOK', "ims") == 1
    assert qcfg_first_value('+QCFG: "ims/ut",0,0,0\r\nOK', "ims/ut") == 0
    assert qcfg_first_value('+QCFG: "nwscanmode",3\r\nOK', "nwscanmode") == 3
    assert cfun_value("+CFUN: 1\r\nOK") == 1
    assert cpin_value("+CPIN: READY\r\nOK") == "READY"
    assert qsimdet_config("+QSIMDET: 1,0\r\nOK") == (1, 0)
    assert qsimdet_config("+QSIMDET: 1,1\r\nOK") == (1, 1)
    assert qsimdet_config("+QSIMDET: 0,0\r\nOK") == (0, 0)
    assert qsimdet_config("+CME ERROR: 100") is None
    assert qsimdet_config("+QSIMDET: 2,9\r\nOK") is None
    assert sim_hotplug_enable_commands("+QSIMDET: 1,0\r\nOK") == [
        SIM_STATUS_URC_ENABLE,
        "AT+QSIMDET=1,0",
    ]
    assert sim_hotplug_enable_commands("+QSIMDET: 1,1\r\nOK") == [
        SIM_STATUS_URC_ENABLE,
        "AT+QSIMDET=1,1",
    ]
    assert sim_hotplug_enable_commands("+QSIMDET: 0,0\r\nOK") == [
        SIM_STATUS_URC_ENABLE,
        "AT+QSIMDET=1,0",
    ]
    assert sim_hotplug_enable_commands("+QSIMDET: 0,1\r\nOK") == [
        SIM_STATUS_URC_ENABLE,
        "AT+QSIMDET=1,1",
    ]
    assert sim_hotplug_enable_commands("+CME ERROR: 100") == []
    assert iccid_value("+ICCID: 89840200011815310980\r\nOK") == (
        "89840200011815310980"
    )
    assert iccid_value("+CME ERROR: 10") is None
    assert registration_value("+CREG: 2,1\r\nOK", "C") == 1
    assert registration_value("+CGREG: 0,5\r\nOK", "CG") == 5
    assert uart1_is_active('+QURCCFG: "urcport","uart1"\r\nOK')

    bad_plan = planned_config_commands(
        '+QCFG: "ims",1,0\r\nOK',
        '+QCFG: "ims/ut",1,1,0\r\nOK',
        '+QCFG: "nwscanmode",3\r\nOK',
    )
    assert bad_plan == [IMS_UT_DISABLE, NETWORK_MODE_AUTO]

    good_plan = planned_config_commands(
        '+QCFG: "ims",0,0\r\nOK',
        '+QCFG: "ims/ut",0,0,0\r\nOK',
        '+QCFG: "nwscanmode",0\r\nOK',
    )
    assert good_plan == []
    assert NETWORK_MODE_AUTO == 'AT+QCFG="nwscanmode",0,0'

    all_commands = [
        ECHO_DISABLE,
        VERBOSE_ERRORS_ENABLE,
        UART1_QUERY,
        UART1_SET,
        IMS_QUERY,
        IMS_UT_QUERY,
        IMS_UT_DISABLE,
        NETWORK_MODE_QUERY,
        NETWORK_MODE_AUTO,
        SIM_STATUS_QUERY,
        SIM_STATUS_URC_ENABLE,
        SIM_DETECT_QUERY,
        *sim_hotplug_enable_commands("+QSIMDET: 1,0\r\nOK"),
        *sim_hotplug_enable_commands("+QSIMDET: 1,1\r\nOK"),
        *sim_hotplug_enable_commands("+QSIMDET: 0,0\r\nOK"),
        IDENTITY_QUERY,
        FIRMWARE_QUERY,
        NETWORK_INFO_QUERY,
        SERVING_CELL_QUERY,
        EXTENDED_ERROR_QUERY,
        USSD_STATUS_QUERY,
        REBOOT_COMMAND,
        *ICCID_QUERY_COMMANDS,
        USSD_CANCEL,
        USSD_101_WITH_DCS,
        USSD_101_WITHOUT_DCS,
        "AT+CFUN?",
        "AT+CPIN?",
        "AT+CREG?",
        "AT+CGREG?",
        "AT+CEREG?",
        "AT+COPS?",
        "AT+CSQ",
    ]
    combined = "\n".join(all_commands).upper()
    for forbidden in (
        "QPRTPARA",
        "EGMR",
        "USBAT",
        "CFUN=4",
        "CFUN=0",
        "COPS=2",
        "AT&F",
        "\nATZ\n",
        'QCFG="IMS",2',
    ):
        assert forbidden not in combined
    assert all_commands.count(REBOOT_COMMAND) == 1
    assert all_commands.count(USSD_101_WITH_DCS) == 1
    assert all_commands.count(USSD_101_WITHOUT_DCS) == 1
    print(
        "SELF-TEST PASSED: safe command set, QSIMDET hot-plug enable, "
        "ICCID presence gate, *101# fallback, no reset-gốc."
    )


def run_cli(ports: Sequence[str]) -> int:
    cancel_event = threading.Event()
    logger = TraceLogger()
    reports: list[RepairReport] = []
    try:
        for port in sorted(set(ports), key=port_sort_key):
            reports.append(repair_port(port, logger, cancel_event))
    except KeyboardInterrupt:
        cancel_event.set()
        print("\nĐã nhận Ctrl+C, đang dừng...")
        return 130

    print(f"Log: {logger.log_path}")
    for report in reports:
        warning_text = (
            f"; cảnh báo: {'; '.join(report.warnings)}"
            if report.warnings
            else ""
        )
        error_text = f"; lỗi: {report.error}" if report.error else ""
        print(
            f"{report.port}: {report.summary} ({report.elapsed_seconds:.1f}s)"
            f"{warning_text}{error_text}"
        )
    return 0 if reports and all(report.success for report in reports) else 1


class GsmFixGui:
    def __init__(self) -> None:
        import tkinter as tk
        from tkinter import messagebox, scrolledtext, ttk

        self.tk = tk
        self.messagebox = messagebox
        self.root = tk.Tk()
        self.root.title("Tool Fix GSM - Safe Repair & Diagnostics")
        self.root.geometry("920x650")
        self.root.minsize(760, 520)
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

        self._events: queue.Queue[tuple[str, object]] = queue.Queue()
        self._cancel_event = threading.Event()
        self._worker: threading.Thread | None = None

        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill=tk.BOTH, expand=True)

        title = ttk.Label(
            outer,
            text=(
                "FIX GSM: dọn USSD + uart1 + tắt IMS/UT + AUTO + "
                "reboot 1 lần"
            ),
            font=("Segoe UI", 14, "bold"),
        )
        title.pack(anchor=tk.W)

        note = ttk.Label(
            outer,
            text=(
                "ICCID chỉ để phát hiện SIM • Có ICCID thì dò *101# • "
                "Tự bật QSIMDET theo polarity modem đang lưu • Không reset gốc/IMEI. "
                "Đóng ToolGSM trước khi chạy."
            ),
            foreground="#444444",
        )
        note.pack(anchor=tk.W, pady=(2, 10))

        ports_frame = ttk.LabelFrame(outer, text="Chọn một hoặc nhiều COM", padding=8)
        ports_frame.pack(fill=tk.X)

        self.tree = ttk.Treeview(
            ports_frame,
            columns=("port", "description", "status"),
            show="headings",
            selectmode="extended",
            height=8,
        )
        self.tree.heading("port", text="COM")
        self.tree.heading("description", text="Thiết bị")
        self.tree.heading("status", text="Trạng thái")
        self.tree.column("port", width=90, stretch=False)
        self.tree.column("description", width=470)
        self.tree.column("status", width=220)
        self.tree.pack(side=tk.LEFT, fill=tk.X, expand=True)

        scrollbar = ttk.Scrollbar(
            ports_frame, orient=tk.VERTICAL, command=self.tree.yview
        )
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        self.tree.configure(yscrollcommand=scrollbar.set)

        actions = ttk.Frame(outer)
        actions.pack(fill=tk.X, pady=10)
        self.refresh_button = ttk.Button(
            actions, text="Làm mới COM", command=self.refresh_ports
        )
        self.refresh_button.pack(side=tk.LEFT)
        self.fix_button = ttk.Button(
            actions, text="FIX COM đã chọn", command=self.start_fix
        )
        self.fix_button.pack(side=tk.LEFT, padx=8)
        self.stop_button = ttk.Button(
            actions, text="Dừng", command=self.stop_fix, state=tk.DISABLED
        )
        self.stop_button.pack(side=tk.LEFT)
        self.progress = ttk.Progressbar(actions, mode="indeterminate")
        self.progress.pack(side=tk.RIGHT, fill=tk.X, expand=True, padx=(20, 0))

        log_frame = ttk.LabelFrame(outer, text="Log trực tiếp", padding=8)
        log_frame.pack(fill=tk.BOTH, expand=True)
        self.log_text = scrolledtext.ScrolledText(
            log_frame,
            wrap=tk.WORD,
            font=("Consolas", 9),
            state=tk.DISABLED,
        )
        self.log_text.pack(fill=tk.BOTH, expand=True)

        self.status_var = tk.StringVar(value="Sẵn sàng.")
        ttk.Label(outer, textvariable=self.status_var).pack(
            anchor=tk.W, pady=(8, 0)
        )

        self.refresh_ports()
        self.root.after(100, self._drain_events)

    def run(self) -> None:
        self.root.mainloop()

    def refresh_ports(self) -> None:
        selected_names = {
            self.tree.item(item, "values")[0] for item in self.tree.selection()
        }
        for item in self.tree.get_children():
            self.tree.delete(item)
        try:
            ports = available_ports()
        except Exception as exc:
            self.messagebox.showerror("Không đọc được COM", str(exc))
            return
        for port, description in ports:
            item = self.tree.insert(
                "", self.tk.END, values=(port, description, "Sẵn sàng")
            )
            if port in selected_names:
                self.tree.selection_add(item)
        self.status_var.set(f"Tìm thấy {len(ports)} cổng COM.")

    def selected_ports(self) -> list[str]:
        return [
            str(self.tree.item(item, "values")[0])
            for item in self.tree.selection()
        ]

    def start_fix(self) -> None:
        ports = self.selected_ports()
        if not ports:
            self.messagebox.showwarning(
                "Chưa chọn COM", "Chọn ít nhất một COM cần fix."
            )
            return
        if self._worker and self._worker.is_alive():
            return

        self._cancel_event = threading.Event()
        self._set_running(True)
        self._worker = threading.Thread(
            target=self._run_worker,
            args=(ports,),
            name="gsm-fix-worker",
            daemon=True,
        )
        self._worker.start()

    def _run_worker(self, ports: Sequence[str]) -> None:
        logger = TraceLogger(output=lambda line: self._events.put(("log", line)))
        reports: list[RepairReport] = []
        for port in sorted(set(ports), key=port_sort_key):
            if self._cancel_event.is_set():
                break
            self._events.put(("port_status", (port, "Đang fix...")))
            report = repair_port(port, logger, self._cancel_event)
            reports.append(report)
            self._events.put(("port_status", (port, report.summary)))
        self._events.put(("done", (reports, logger.log_path)))

    def stop_fix(self) -> None:
        self._cancel_event.set()
        self.status_var.set("Đang dừng an toàn...")

    def _set_running(self, running: bool) -> None:
        state = self.tk.DISABLED if running else self.tk.NORMAL
        self.refresh_button.configure(state=state)
        self.fix_button.configure(state=state)
        self.stop_button.configure(
            state=self.tk.NORMAL if running else self.tk.DISABLED
        )
        if running:
            self.progress.start(12)
            self.status_var.set("Đang fix. Không rút cáp hoặc tắt nguồn...")
        else:
            self.progress.stop()

    def _append_log(self, line: str) -> None:
        self.log_text.configure(state=self.tk.NORMAL)
        self.log_text.insert(self.tk.END, line + "\n")
        self.log_text.see(self.tk.END)
        self.log_text.configure(state=self.tk.DISABLED)

    def _update_port_status(self, port: str, status: str) -> None:
        for item in self.tree.get_children():
            values = list(self.tree.item(item, "values"))
            if values and str(values[0]).upper() == port.upper():
                values[2] = status
                self.tree.item(item, values=values)
                break

    def _drain_events(self) -> None:
        try:
            while True:
                event, payload = self._events.get_nowait()
                if event == "log":
                    self._append_log(str(payload))
                elif event == "port_status":
                    port, status = payload
                    self._update_port_status(str(port), str(status))
                elif event == "done":
                    reports, log_path = payload
                    self._set_running(False)
                    successes = sum(1 for report in reports if report.success)
                    self.status_var.set(
                        f"Hoàn tất {successes}/{len(reports)} COM. Log: {log_path}"
                    )
                    if reports and all(report.success for report in reports):
                        self.messagebox.showinfo(
                            "Fix hoàn tất",
                            f"Đã fix {successes} COM.\nLog: {log_path}",
                        )
                    elif reports:
                        self.messagebox.showwarning(
                            "Fix chưa hoàn tất",
                            f"Thành công {successes}/{len(reports)} COM.\n"
                            f"Xem log: {log_path}",
                        )
        except queue.Empty:
            pass
        finally:
            self.root.after(100, self._drain_events)

    def _on_close(self) -> None:
        if self._worker and self._worker.is_alive():
            if not self.messagebox.askyesno(
                "Đang fix", "Dừng quy trình đang chạy và đóng Tool Fix?"
            ):
                return
            self._cancel_event.set()
        self.root.destroy()


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Fix Quectel GSM: clean stale USSD, uart1, IMS/UT off, "
            "network AUTO, QSIMDET hot-plug enable, one reboot. "
            "If ICCID is present, query *101#."
        )
    )
    parser.add_argument(
        "--port",
        "--ports",
        dest="ports",
        nargs="+",
        help="COM cần fix, ví dụ --port COM105 hoặc --ports COM94 COM105",
    )
    parser.add_argument(
        "--all",
        action="store_true",
        help="Fix tất cả COM đang được Windows nhận diện",
    )
    parser.add_argument(
        "--list",
        action="store_true",
        help="Liệt kê COM rồi thoát",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="Chạy test parser/allowlist, không mở COM",
    )
    return parser.parse_args(argv)


def configure_console_encoding() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (OSError, ValueError):
                pass


def main(argv: Sequence[str] | None = None) -> int:
    configure_console_encoding()
    args = parse_args(argv if argv is not None else sys.argv[1:])
    if args.self_test:
        run_self_test()
        return 0

    try:
        require_pyserial()
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    if args.list:
        for port, description in available_ports():
            print(f"{port}\t{description}")
        return 0

    ports: list[str] = list(args.ports or [])
    if args.all:
        ports.extend(port for port, _description in available_ports())
    if ports:
        return run_cli(ports)

    try:
        GsmFixGui().run()
        return 0
    except Exception as exc:
        print(f"Không mở được giao diện: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
