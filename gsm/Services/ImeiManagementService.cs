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
    public static readonly string[] FakeTacs = new[] {
        "35293630", // iPhone 14 Pro Max
        "35307371", // iPhone 14
        "35293425", // iPhone 14 Pro
        "35443477", // iPhone 15 Pro
        "35684784", // iPhone 15 Plus
        "35300911", // iPhone 12 Pro Max
        "35689020", // Samsung Galaxy S23 Ultra
        "35205562", // Samsung Galaxy S22 Ultra
        "35848511", // Samsung Galaxy S21 Ultra 5G
        "35623011", // Samsung Galaxy Note 20 Ultra
        "35398226", // iPhone 13 Pro Max
        "35874288", // iPhone 15
        "35919376", // iPhone 15 Pro Max
        "35179311", // Samsung Galaxy Z Fold 4
        "35385711", // Samsung Galaxy Z Flip 4
        "35424597", // Google Pixel 8 Pro
        "35639611", // Google Pixel 7 Pro
        "35824511", // Google Pixel 6
        "86129004", // Xiaomi 13 Pro
        "86333405", // Oppo Reno 6
        "86542704", // Oppo Find X3 Pro
        "86770205", // Oppo Find X5 Pro
        "86744805", // Vivo X70 Pro
        "86086705"  // Huawei P50 Pro
    };

    public static bool IsFakeImei(string imei)
    {
        if (string.IsNullOrWhiteSpace(imei) || imei.Length < 8) return false;
        string tac = imei.Substring(0, 8);
        foreach (var t in FakeTacs)
        {
            if (tac == t) return true;
        }
        return false;
    }

    public static string GenerateRandomImei()
    {
        var random = new Random();
        string tac = FakeTacs[random.Next(FakeTacs.Length)];
        string snr = random.Next(0, 999999).ToString("D6");
        string imeiWithoutCheck = tac + snr;
        
        int sum = 0;
        for (int i = 0; i < 14; i++)
        {
            int digit = imeiWithoutCheck[i] - '0';
            if (i % 2 != 0)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        int checkDigit = (10 - (sum % 10)) % 10;
        return imeiWithoutCheck + checkDigit;
    }

    public static string GetDeviceNameFromImei(string imei)
    {
        if (string.IsNullOrWhiteSpace(imei) || imei.Length < 8) return "Mặc định (GSM Modem)";
        
        string tac = imei.Substring(0, 8);
        return tac switch
        {
            "35293630" => "iPhone 14 Pro Max",
            "35307371" => "iPhone 14",
            "35293425" => "iPhone 14 Pro",
            "35443477" => "iPhone 15 Pro",
            "35684784" => "iPhone 15 Plus",
            "35300911" => "iPhone 12 Pro Max",
            "35689020" => "Samsung Galaxy S23 Ultra",
            "35205562" => "Samsung Galaxy S22 Ultra",
            "35848511" => "Samsung Galaxy S21 Ultra 5G",
            "35623011" => "Samsung Galaxy Note 20 Ultra",
            "35398226" => "iPhone 13 Pro Max",
            "35874288" => "iPhone 15",
            "35919376" => "iPhone 15 Pro Max",
            "35179311" => "Samsung Galaxy Z Fold 4",
            "35385711" => "Samsung Galaxy Z Flip 4",
            "35424597" => "Google Pixel 8 Pro",
            "35639611" => "Google Pixel 7 Pro",
            "35824511" => "Google Pixel 6",
            "86129004" => "Xiaomi 13 Pro",
            "86333405" => "Oppo Reno 6",
            "86542704" => "Oppo Find X3 Pro",
            "86770205" => "Oppo Find X5 Pro",
            "86744805" => "Vivo X70 Pro",
            "86086705" => "Huawei P50 Pro",
            // Legacy / Older generated TACs from previous versions
            "35198031" => "Samsung Galaxy S23",
            "35435973" => "Samsung Galaxy S23 Ultra (Cũ)",
            "35925411" => "iPhone 12",
            "35483211" => "Samsung Galaxy S21",
            "35832011" => "iPhone 13",
            "35384110" => "iPhone 11",
            "35303609" => "iPhone X",
            "86940804" => "Xiaomi Redmi Note 10",
            _ => "Mặc định (GSM Modem)"
        };
    }

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

    private bool IsValidImei(string imei)
    {
        if (string.IsNullOrWhiteSpace(imei)) return false;
        var clean = new string(imei.Where(char.IsDigit).ToArray());
        return clean.Length == 15;
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
            // Nếu chế độ Nạp SIM Mới đang bật, bỏ qua bước chặn này để lưu trực tiếp IMEI đã tráng
            if (cachedEntry == null && settings.BlockUnknownSims && !settings.EnableNewSimIntakeMode)
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
                string candidateImei = NormalizeImei(cachedEntry.Imei);
                
                dispatcherInvoke(() => port.DeviceName = GetDeviceNameFromImei(candidateImei));

                if (IsValidImei(candidateImei))
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
            // Mặc định (GSM Modem) khi Restore IMEI không áp dụng
            else
            {
                dispatcherInvoke(() => port.DeviceName = GetDeviceNameFromImei(currentImei));

                if (cachedEntry != null && !settings.EnableImeiRestore)
                {
                    Log($"[{portName}] Đã có bản Backup IMEI nhưng tính năng Khôi phục (Restore) đang tắt. Giữ nguyên IMEI gốc trên mạch.");
                }
                else if (cachedEntry == null)
                {
                    if (!IsValidImei(NormalizeImei(currentImei)))
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
                            SourceFile = settings.EnableNewSimIntakeMode ? "new-sim-intake" : "auto-learn"
                        };
                        saveBackupEntry(newEntry);
                        
                        if (settings.EnableNewSimIntakeMode)
                        {
                            Log($"[{portName}] (Nạp SIM Mới) Đã ghi nhận Fake IMEI tráng sẵn: {currentImei} gắn với CCID: {ccid}.", "SUCCESS");
                        }
                        else
                        {
                            Log($"[{portName}] Cắm lần đầu, tự động ghi nhận IMEI gốc: {currentImei} gắn với CCID: {ccid} vào file backup.", "SUCCESS");
                        }

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
                bool isUnsupported = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    Log($"[{portName}] Thử ghi IMEI lần {attempt}/3...");

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

                        Log($"[{portName}] Đã gửi lệnh khởi động lại modem (AT+CFUN=1,1) để áp dụng IMEI mới triệt để vào Baseband.", "INFO");
                        string cfun1 = await _modemService.SendCommandAsync(portName, "AT+CFUN=1,1", 30000, silent: true);
                        
                        // Trả về Applied ngay lập tức, vì modem sẽ mất kết nối USB khi khởi động lại
                        return new ImeiProcessResult { Status = ImeiProcessStatus.Applied, FinalImei = targetImei };
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
                    string errorMsg = isUnsupported ? "Mạch không hỗ trợ đổi IMEI (Khóa Firmware)" : "Ghi đè IMEI thất bại";
                    return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = errorMsg };
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

            string checkFinalImei = string.Empty;
            for (int i = 0; i < 3; i++)
            {
                string checkFinalResp = await _modemService.SendCommandAsync(portName, "AT+CGSN", 10000, silent: true);
                checkFinalImei = NormalizeImei(checkFinalResp);
                if (!string.IsNullOrEmpty(checkFinalImei)) break;
                await Task.Delay(1000);
            }
            
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

    public static void AppendSpoofImeiExcel(string portName, string ccid, string imei, string phoneNumber, string deviceName, string createdAt)
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
        }
        catch (Exception)
        {
            // Fail silently or handle
        }
    }
}
