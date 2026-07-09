@echo off
set ADB="C:\Users\congn\AppData\Local\Android\Sdk\platform-tools\adb.exe"

echo ============================================
echo   TAT PROXY ADB (USB)
echo ============================================

echo [*] Xoa proxy tren dien thoai...
%ADB% shell settings put global http_proxy :0
%ADB% shell settings delete global http_proxy
%ADB% shell settings delete global global_http_proxy_host
%ADB% shell settings delete global global_http_proxy_port

echo [*] Xoa reverse port cap USB...
%ADB% reverse --remove-all

echo.
echo [DONE] Da go bo proxy. Dien thoai da tro ve mang binh thuong.
echo Bam phim bat ky de thoat...
pause >nul
