@echo off
set ADB="C:\Users\congn\AppData\Local\Android\Sdk\platform-tools\adb.exe"

echo ============================================
echo   BAT DAU PROXY QUA CAP ADB (USB)
echo ============================================

echo [*] Xoa reverse port cu (neu co)...
%ADB% reverse --remove-all

echo [*] Forward port 8888 tu dien thoai ve PC (port 8080 cua mitmproxy)...
%ADB% reverse tcp:8888 tcp:8080

echo [*] Thiet lap Global Proxy tren dien thoai ve 127.0.0.1:8888...
%ADB% shell settings put global http_proxy 127.0.0.1:8888

echo.
echo [DONE] Tat ca traffic tu dien thoai da duoc day qua cap USB ve mitmproxy (PC: 8080)!
echo Bam phim bat ky de thoat...
pause >nul
