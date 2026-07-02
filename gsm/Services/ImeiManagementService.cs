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
            var cachedEntry = getBackupEntry(ccid);

            // [FIX LOGIC]: Đưa ưu tiên chặn SIM lạ lên hàng đầu (Tính năng 3)
            if (cachedEntry == null && settings.BlockUnknownSims)
            {
                Log($"[{portName}] SIM mới chưa có trong kho IMEI. Đã chặn, chờ chấp thuận thủ công.", "WARNING");

                return new ImeiProcessResult
                {
                    Status = ImeiProcessStatus.SecurityBlocked,
                    ErrorMessage = "SIM mới chưa được chấp thuận"
                };
            }

            // Ưu tiên 2: Phục hồi từ backup nếu có (và nếu tính năng Restore được bật)
            if (settings.EnableImeiRestore && cachedEntry != null)
            {
                dispatcherInvoke(() => port.DeviceName = "Mặc định (GSM Modem)");
                
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
            // Ưu tiên 1: Tạo/Sử dụng fake IMEI cho SIM mới hoặc khi Restore tắt
            else if (settings.EnableDeviceSpoofing)
            {
                var identity = DeviceSpoofingService.GetOrCreateByCcid(ccid, portName, port.PhoneNumber);
                targetImei = NormalizeImei(identity.AssignedImei);
                targetSource = $"SPOOF ({identity.DeviceName})";

                if (cachedEntry == null && DeviceSpoofingService.IsValidImei(targetImei))
                {
                    saveBackupEntry(new SimBackupEntry
                    {
                        Ccid = ccid,
                        Imei = targetImei,
                        PhoneNumber = port.PhoneNumber,
                        CreatedAt = identity.CreatedAt,
                        LicenseKeySuffix = string.Empty,
                        KeyMismatch = "false",
                        SourceFile = "device-spoof"
                    });

                    Log($"[{portName}] [SPOOF_BACKUP] Đã lưu Fake IMEI {targetImei} cho CCID {ccid} vào imei_backup.csv.", "SUCCESS");

                    AppendSpoofImeiExcel(portName, ccid, targetImei, port.PhoneNumber, identity.DeviceName, identity.CreatedAt);
                }

                dispatcherInvoke(() => port.DeviceName = identity.DeviceName);
                Log($"[{portName}] [DEVICE_SPOOF] SIM CCID={ccid} định danh: {identity.DeviceName} | IMEI mục tiêu: {targetImei}");
            }
            // Mặc định (GSM Modem) khi cả Fake IMEI và Restore IMEI đều không áp dụng
            else
            {
                dispatcherInvoke(() => port.DeviceName = "Mặc định (GSM Modem)");

                if (cachedEntry != null && !settings.EnableImeiRestore)
                {
                    Log($"[{portName}] Đã có bản Backup IMEI nhưng tính năng Khôi phục (Restore) đang tắt. Giữ nguyên IMEI gốc trên mạch.");
                }
                else if (cachedEntry == null)
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
                        Log($"[{portName}] Cắm lần đầu, tự động ghi nhận IMEI gốc: {currentImei} gắn với CCID: {ccid} vào file backup.", "SUCCESS");

                        dispatcherInvoke(() =>
                        {
                            port.CreatedAt = newEntry.CreatedAt;
                            port.LicenseKeySuffix = newEntry.LicenseKeySuffix;
                            port.KeyMismatch = newEntry.KeyMismatch;
                        });
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

    private void AppendSpoofImeiExcel(string portName, string ccid, string imei, string phoneNumber, string deviceName, string createdAt)
    {
        try
        {
            string filePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "spoof_imei_backup.xlsx");
            OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            
            using var package = new OfficeOpenXml.ExcelPackage(new System.IO.FileInfo(filePath));
            var worksheet = package.Workbook.Worksheets.Count > 0 ? package.Workbook.Worksheets[0] : package.Workbook.Worksheets.Add("Spoof IMEI");
            
            if (worksheet.Dimension == null)
            {
                worksheet.Cells[1, 1].Value = "CCID";
                worksheet.Cells[1, 2].Value = "IMEI";
                worksheet.Cells[1, 3].Value = "Phone Number";
                worksheet.Cells[1, 4].Value = "Device Name";
                worksheet.Cells[1, 5].Value = "Created At";
            }
            
            int row = worksheet.Dimension?.End.Row + 1 ?? 2;
            worksheet.Cells[row, 1].Value = ccid;
            worksheet.Cells[row, 2].Value = imei;
            worksheet.Cells[row, 3].Value = phoneNumber;
            worksheet.Cells[row, 4].Value = deviceName;
            worksheet.Cells[row, 5].Value = createdAt;
            
            package.Save();
            Log($"[{portName}] Đã ghi bổ sung Fake IMEI {imei} vào file spoof_imei_backup.xlsx.", "SUCCESS");
        }
        catch (Exception ex)
        {
            Log($"[{portName}] Lỗi khi lưu spoof IMEI ra Excel: {ex.Message}", "ERROR");
        }
    }
}
