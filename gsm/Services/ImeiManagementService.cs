using System;
using System.Threading.Tasks;
using gsm.Models;

namespace gsm.Services;

public enum ImeiProcessStatus
{
    Matched,
    Applied,
    SecurityBlocked,
    Error
}

public class ImeiProcessResult
{
    public ImeiProcessStatus Status { get; set; }
    public string FinalImei { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public class ImeiManagementService
{
    private readonly IGsmModemService _modemService;
    private readonly Action<string, string> _logAction;

    public ImeiManagementService(IGsmModemService modemService, Action<string, string> logAction)
    {
        _modemService = modemService;
        _logAction = logAction;
    }

    private void Log(string message, string level = "INFO")
    {
        _logAction?.Invoke(message, level);
    }

    private string NormalizeImei(string? imei)
    {
        if (string.IsNullOrWhiteSpace(imei)) return string.Empty;
        var digits = new System.Text.StringBuilder();
        foreach (var c in imei)
        {
            if (char.IsDigit(c))
                digits.Append(c);
        }
        return digits.ToString();
    }

    public async Task<ImeiProcessResult> ProcessImeiAsync(
        SimPort port, 
        string ccid, 
        string currentImei, 
        AppSettings settings,
        Func<string, SimBackupEntry?> getBackupEntry, 
        Action<SimBackupEntry> saveBackupEntry,
        Action<Action> dispatcherInvoke)
    {
        string targetImei = string.Empty;
        string expectedImei = currentImei;
        string targetSource = string.Empty;
        string portName = port.PortName;
        
        try
        {
            if (settings.EnableDeviceSpoofing)
            {
                var identity = DeviceSpoofingService.GetOrCreateByCcid(ccid, portName, port.PhoneNumber);
                targetImei = NormalizeImei(identity.AssignedImei);
                targetSource = $"SPOOF ({identity.DeviceName})";

                dispatcherInvoke(() => port.DeviceName = identity.DeviceName);
                Log($"[{portName}] [DEVICE_SPOOF] SIM CCID={ccid} định danh: {identity.DeviceName} | IMEI mục tiêu: {targetImei}");
            }
            else
            {
                dispatcherInvoke(() => port.DeviceName = "Mặc định (GSM Modem)");

                var cachedEntry = getBackupEntry(ccid);
                if (cachedEntry != null)
                {
                    if (settings.EnableImeiRestore)
                    {
                        string candidateImei = NormalizeImei(cachedEntry.Imei);
                        if (DeviceSpoofingService.IsValidImei(candidateImei))
                        {
                            targetImei = candidateImei;
                            targetSource = string.IsNullOrWhiteSpace(cachedEntry.SourceFile) ? "imei_backup.csv" : cachedEntry.SourceFile;
                            
                            dispatcherInvoke(() =>
                            {
                                if (!string.IsNullOrWhiteSpace(cachedEntry.PhoneNumber))
                                {
                                    port.PhoneNumber = cachedEntry.PhoneNumber;
                                }
                                port.CreatedAt = cachedEntry.CreatedAt;
                                port.LicenseKeySuffix = cachedEntry.LicenseKeySuffix;
                                port.KeyMismatch = cachedEntry.KeyMismatch;
                            });
                        }
                        else
                        {
                            Log($"[{portName}] Bản Backup IMEI không hợp lệ ({candidateImei}). Bỏ qua Restore để bảo vệ thiết bị.", "ERROR");
                        }
                    }
                    else
                    {
                        Log($"[{portName}] Đã có bản Backup IMEI nhưng tính năng Khôi phục (Restore) đang tắt. Giữ nguyên IMEI gốc trên mạch.");
                    }
                }
                else
                {
                    if (settings.EnableImeiBackup)
                    {
                        if (!DeviceSpoofingService.IsValidImei(NormalizeImei(currentImei)))
                        {
                            Log($"[{portName}] IMEI gốc hiện tại ({currentImei}) không hợp lệ (sai độ dài hoặc checksum). Từ chối tạo bản Backup tự động.", "WARNING");
                        }
                        else
                        {
                            var newEntry = new SimBackupEntry
                        {
                            Ccid = ccid,
                            Imei = currentImei,
                            PhoneNumber = port.PhoneNumber,
                            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            LicenseKeySuffix = string.Empty,
                            KeyMismatch = "false",
                            SourceFile = "auto-learn"
                        };
                        saveBackupEntry(newEntry);
                        Log($"[{portName}] Cắm lần đầu, lưu IMEI gốc: {currentImei} gắn với CCID: {ccid} vào file backup.", "SUCCESS");

                        dispatcherInvoke(() =>
                        {
                            port.CreatedAt = newEntry.CreatedAt;
                            port.LicenseKeySuffix = newEntry.LicenseKeySuffix;
                            port.KeyMismatch = newEntry.KeyMismatch;
                        });
                        }
                    }
                    else
                    {
                        Log($"[{portName}] Tính năng Fake IMEI và Backup đều tắt. Giữ nguyên IMEI gốc trên mạch.");
                    }
                }
            }

            if (string.IsNullOrEmpty(targetImei))
            {
                targetImei = currentImei;
            }

            expectedImei = targetImei;

            if (!string.IsNullOrEmpty(targetImei) && targetImei != currentImei)
            {
                Log($"[{portName}] [IMEI_TARGET] source={targetSource} CCID={ccid} target_imei={targetImei}");
                Log($"[{portName}] [IMEI_CHANGE] IMEI hiện tại ({currentImei}) khác mục tiêu ({targetImei}). Bắt đầu ghi đè...", "WARNING");
                
                string cfun0 = await _modemService.SendCommandAsync(portName, "AT+CFUN=0", 10000, silent: true);
                if (!cfun0.Contains("OK"))
                {
                    Log($"[{portName}] Tắt sóng (AT+CFUN=0) thất bại. Ngừng quá trình ghi IMEI để đảm bảo an toàn.", "ERROR");
                    return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.RadioOffFailed };
                }
                
                await Task.Delay(1000);

                bool success = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    Log($"[{portName}] Thử ghi IMEI lần {attempt}/3...");

                    bool isUnsupported = false;
                    bool isDisconnected = false;
                    string writeResp = await _modemService.SendCommandAsync(portName, $"AT+EGMR=1,7,\"{targetImei}\"", 30000);
                    if (IsConnectionError(writeResp))
                    {
                        isDisconnected = true;
                    }
                    else if (writeResp.Contains("ERROR") && !IsTemporaryCommandError(writeResp))
                    {
                        Log($"[{portName}] Thử lệnh AT+EGMR thất bại, chuyển sang AT+SIMEI...", "INFO");
                        string simeiResp = await _modemService.SendCommandAsync(portName, $"AT+SIMEI=\"{targetImei}\"", 30000);
                        if (IsConnectionError(simeiResp))
                        {
                            isDisconnected = true;
                        }
                        else if (simeiResp.Contains("ERROR") && !IsTemporaryCommandError(simeiResp))
                        {
                            isUnsupported = true;
                        }
                    }

                    string finalImeiResp = await _modemService.SendCommandAsync(portName, "AT+CGSN", 10000, silent: true);
                    string finalImei = NormalizeImei(finalImeiResp);

                    if (finalImei == targetImei)
                    {
                        success = true;
                        dispatcherInvoke(() => port.Imei = targetImei);
                        Log($"[{portName}] Ghi đè IMEI thành công ở lần thử {attempt}: {targetImei}", "SUCCESS");

                        string cfun1 = await _modemService.SendCommandAsync(portName, "AT+CFUN=1", 30000);
                        if (!cfun1.Contains("OK"))
                        {
                            Log($"[{portName}] Bật sóng (AT+CFUN=1) thất bại sau khi ghi IMEI.", "ERROR");
                            return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.RadioOnFailed };
                        }
                        await Task.Delay(2000);
                        break;
                    }
                    else
                    {
                        if (isDisconnected)
                        {
                            Log($"[{portName}] Mất kết nối cổng COM trong quá trình ghi. Hủy Retry.", "ERROR");
                            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "Mất kết nối cổng COM" };
                        }
                        else if (isUnsupported)
                        {
                            Log($"[{portName}] Cả AT+EGMR và AT+SIMEI đều trả về ERROR. Module bị khóa Firmware (Unsupported). Hủy Retry.", "ERROR");
                            break; 
                        }
                        else
                        {
                            Log($"[{portName}] Ghi đè IMEI thất bại ở lần thử {attempt} (Đọc lại: {finalImei}). Giữ sóng tắt an toàn.", "ERROR");
                        }
                    }
                }

                if (!success)
                {
                    Log($"[{portName}] Đã thử ghi IMEI 3 lần nhưng không thành công.", "ERROR");
                    return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.WrongImei };
                }
            }
            else
            {
                if (targetImei == currentImei && !string.IsNullOrEmpty(currentImei))
                {
                    Log($"[{portName}] IMEI khớp với mục tiêu: {currentImei}", "SUCCESS");
                }
                Log($"[{portName}] Đang bật sóng (AT+CFUN=1)...");
                await _modemService.SendCommandAsync(portName, "AT+CFUN=1", 30000);
                await Task.Delay(1500);
            }

            string checkFinalResp = await _modemService.SendCommandAsync(portName, "AT+CGSN", 10000, silent: true);
            string checkFinalImei = NormalizeImei(checkFinalResp);
            
            bool matched = (checkFinalImei == expectedImei) && !string.IsNullOrEmpty(checkFinalImei);
            Log($"[{portName}] [IMEI_FINAL] current={checkFinalImei}, expected={expectedImei}, matched={matched.ToString().ToLowerInvariant()}", matched ? "SUCCESS" : "ERROR");

            if (matched)
            {
                bool wasApplied = (!string.IsNullOrEmpty(targetImei) && targetImei != currentImei);
                return new ImeiProcessResult 
                { 
                    Status = wasApplied ? ImeiProcessStatus.Applied : ImeiProcessStatus.Matched, 
                    FinalImei = checkFinalImei
                };
            }
            else
            {
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.WrongImei };
            }
        }
        catch (Exception ex)
        {
            Log($"[{portName}] Lỗi ngoại lệ trong quá trình xử lý IMEI: {ex.Message}", "ERROR");
            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = ex.Message };
        }
    }

    private static bool IsTemporaryCommandError(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        string respLower = response.ToLowerInvariant();
        return respLower.Contains("timeout") ||
               respLower.Contains("another command") ||
               respLower.Contains("semaphore missing");
    }

    private static bool IsConnectionError(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        string respLower = response.ToLowerInvariant();
        return respLower.Contains("port not open") ||
               respLower.Contains("rút cáp");
    }
}
