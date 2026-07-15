using System;
using System.Threading.Tasks;
using gsm.Models;

namespace gsm.Services;

public enum ImeiProcessStatus
{
    Matched,
    Applied,
    /// <summary>SIM bị chặn bảo mật (IMEI sai, lỗi xác thực).</summary>
    SecurityBlocked,
    /// <summary>SIM mới chưa có trong kho backup, đang chờ user chấp nhận thủ công.</summary>
    WaitingAccept,
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
        string tac = FakeTacs[Random.Shared.Next(FakeTacs.Length)];
        string snr = Random.Shared.Next(0, 1_000_000).ToString("D6");
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
    private readonly Action<string, string>? _logAction;

    public ImeiManagementService(IGsmModemService modemService, Action<string, string>? logAction = null)
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
        Action<Action> dispatcherInvoke,
        bool forceAccept = false,
        CancellationToken ct = default,
        Func<Task<bool>>? validateIdentityAsync = null,
        Func<string, bool>? imeiAlreadyAssigned = null,
        string? explicitTargetImei = null)
    {
        string targetImei = string.Empty;
        string expectedImei = currentImei;
        string targetSource = string.Empty;
        string portName = port.PortName;
        
        try
        {
            async Task<bool> SessionIsValidAsync()
            {
                ct.ThrowIfCancellationRequested();
                return validateIdentityAsync == null || await validateIdentityAsync();
            }

            if (!await SessionIsValidAsync())
                return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã thay đổi trong lúc xử lý" };

            var cachedEntry = getBackupEntry(ccid);
            bool hasValidBackup = cachedEntry != null && IsValidImei(NormalizeImei(cachedEntry.Imei));
            bool isHardwareImeiValid = IsValidImei(NormalizeImei(currentImei));

            // Quyết định xem SIM này có cần được xử lý như SIM mới (chờ chấp nhận để tráng IMEI mới) hay không
            bool treatAsNewSim = (cachedEntry == null) || (!isHardwareImeiValid && !hasValidBackup);

            // 1. Kiểm tra chặn/chờ duyệt đối với SIM mới
            if (treatAsNewSim && !port.IsRebooting && !forceAccept)
            {
                if (settings.EnableNewSimIntakeMode)
                {
                    // Chế độ nạp SIM mới: Trả về WaitingAccept (đợi user duyệt thủ công)
                    return new ImeiProcessResult
                    {
                        Status = ImeiProcessStatus.WaitingAccept,
                        ErrorMessage = "SIM mới chưa trong hệ thống, đang chờ chấp nhận",
                        FinalImei = currentImei
                    };
                }
                else if (settings.BlockUnknownSims)
                {
                    Log($"[{portName}] SIM mới chưa được đăng ký trong hệ thống. Đã chặn, chờ duyệt thủ công.", "WARNING");
                    return new ImeiProcessResult
                    {
                        Status = ImeiProcessStatus.SecurityBlocked,
                        ErrorMessage = "SIM mới chưa được chấp thuận"
                    };
                }
            }

            // 2. Xác định IMEI mục tiêu (targetImei)
            string normalizedExplicitTarget = NormalizeImei(explicitTargetImei);
            if (IsValidImei(normalizedExplicitTarget))
            {
                if (imeiAlreadyAssigned?.Invoke(normalizedExplicitTarget) == true)
                    return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "IMEI mục tiêu đang được gán cho SIM khác" };
                targetImei = normalizedExplicitTarget;
                targetSource = "manual-verified";
            }
            else if (settings.EnableImeiRestore && hasValidBackup)
            {
                // Tráng phục hồi từ file backup cũ
                string candidateImei = NormalizeImei(cachedEntry!.Imei);
                targetImei = candidateImei;
                targetSource = string.IsNullOrWhiteSpace(cachedEntry.SourceFile) ? "imei_backup.csv" : cachedEntry.SourceFile;
                
                dispatcherInvoke(() =>
                {
                    port.DeviceName = GetDeviceNameFromImei(candidateImei);
                    if (!string.IsNullOrWhiteSpace(cachedEntry.PhoneNumber))
                    {
                        port.PhoneNumber = cachedEntry.PhoneNumber;
                    }
                    port.CreatedAt = cachedEntry.CreatedAt;
                });
            }
            else
            {
                // Giữ nguyên thiết bị UI hiển thị theo IMEI hiện tại
                dispatcherInvoke(() => port.DeviceName = GetDeviceNameFromImei(currentImei));

                if (cachedEntry != null && !settings.EnableImeiRestore)
                {
                    Log($"[{portName}] Đã có bản Backup IMEI nhưng tính năng Khôi phục đang tắt. Giữ nguyên IMEI gốc.");
                }
                else if (cachedEntry == null && isHardwareImeiValid && !forceAccept && settings.EnableNewSimIntakeMode)
                {
                    // SIM mới cắm lần đầu, có IMEI gốc hợp lệ -> Lưu lại IMEI gốc của mạch để làm backup
                    var newEntry = new SimBackupEntry
                    {
                        Ccid = ccid,
                        Imei = currentImei,
                        PhoneNumber = port.PhoneNumber,
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        SourceFile = "auto-learn",
                        SimRegDate = port.SimRegDate
                    };
                    saveBackupEntry(newEntry);
                    
                    Log($"[{portName}] Cắm lần đầu, tự động ghi nhận IMEI gốc hợp lệ: {currentImei} vào file backup.", "SUCCESS");

                    dispatcherInvoke(() =>
                    {
                        port.CreatedAt = newEntry.CreatedAt;
                    });
                }
            }

            // 3. Nếu cần tráng IMEI mới (SIM mới được chấp nhận hoặc IMEI hiện tại bị hỏng/lỗi mà không có backup)
            if (string.IsNullOrEmpty(targetImei) && (forceAccept || !settings.EnableNewSimIntakeMode))
            {
                // Tạo IMEI ngẫu nhiên hợp lệ đầu 35 hoặc 86
                string randomImei;
                int generationAttempts = 0;
                do
                {
                    randomImei = GenerateRandomImei();
                    generationAttempts++;
                }
                while (imeiAlreadyAssigned?.Invoke(randomImei) == true && generationAttempts < 100);

                if (imeiAlreadyAssigned?.Invoke(randomImei) == true)
                    return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "Không tạo được IMEI duy nhất" };
                targetImei = randomImei;
                targetSource = "auto-generation";
                Log($"[{portName}] Sinh IMEI ngẫu nhiên mới để tráng: {targetImei} (Nguồn: {targetSource})", "SUCCESS");
            }

            // 4. Nếu cuối cùng không có targetImei (ví dụ SIM bị chặn chưa được duyệt), giữ nguyên IMEI hiện tại
            if (string.IsNullOrEmpty(targetImei))
            {
                targetImei = currentImei;
            }

            expectedImei = targetImei;

            // 5. Tiến hành ghi IMEI lên modem nếu khác với IMEI hiện tại
            if (!string.IsNullOrEmpty(targetImei) && targetImei != currentImei)
            {
                Log($"[{portName}] [IMEI_TARGET] source={targetSource} CCID={ccid} target_imei={targetImei}");
                Log($"[{portName}] [IMEI_CHANGE] IMEI hiện tại ({currentImei}) khác mục tiêu ({targetImei}). Bắt đầu ghi đè...", "WARNING");
                
                string cfun0 = await _modemService.SendCommandAsync(portName, "AT+CFUN=0", 10000, silent: true);
                if (!cfun0.Contains("OK"))
                {
                    Log($"[{portName}] Tắt sóng (AT+CFUN=0) thất bại. Hủy ghi IMEI.", "ERROR");
                    return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.RadioOffFailed };
                }
                
                await Task.Delay(1000, ct);

                bool success = false;
                bool isUnsupported = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (!await SessionIsValidAsync())
                        return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã bị rút hoặc thay đổi trước khi ghi IMEI" };

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
                        if (!await SessionIsValidAsync())
                            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã thay đổi sau khi ghi IMEI" };

                        success = true;
                        dispatcherInvoke(() => port.Imei = targetImei);
                        Log($"[{portName}] Ghi đè IMEI thành công ở lần thử {attempt}: {targetImei}", "SUCCESS");

                        var newEntry = new SimBackupEntry
                        {
                            Ccid = ccid,
                            Imei = targetImei,
                            PhoneNumber = port.PhoneNumber,
                            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            SourceFile = targetSource,
                            SimRegDate = port.SimRegDate
                        };
                        saveBackupEntry(newEntry);

                        // Giữ radio ở CFUN=0. Caller phải xác minh lại CCID/IMEI rồi mới
                        // được bật CFUN=1; tránh SIM mới lên mạng trong cửa sổ chưa xác thực.
                        Log($"[{portName}] IMEI đã ghi và xác minh; tiếp tục giữ radio tắt chờ xác minh CCID cuối.", "INFO");
                        
                        // Tr? v? Applied (không phải Matched) vì IMEI đã thành công được thay đổi
                        // Caller tiếp tục qua cổng xác minh và cấu hình offline trước khi bật radio.
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
                            Log($"[{portName}] Mạch không hỗ trợ ghi IMEI (Unsupported). Hủy Retry.", "ERROR");
                            break; 
                        }
                        else
                        {
                            Log($"[{portName}] Ghi đè IMEI thất bại ở lần thử {attempt} (Đọc lại: {finalImei}). Giữ sóng tắt.", "ERROR");
                        }
                    }
                }

                if (!success)
                {
                    Log($"[{portName}] Đã thử ghi IMEI 3 lần không thành công.", "ERROR");
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
                    // QUAN TRỌNG: Không gọi AT+CFUN=1 ở đây.
                    // CompletePortInitializationAsync sẽ cấu hình offline, xác minh lại danh tính
                    // rồi mới bật CFUN=1. Không được bật radio trực tiếp tại service này.
                }

            string checkFinalImei = string.Empty;
            for (int i = 0; i < 3; i++)
            {
                if (!await SessionIsValidAsync())
                    return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã thay đổi trước bước xác minh cuối" };
                string checkFinalResp = await _modemService.SendCommandAsync(portName, "AT+CGSN", 10000, silent: true);
                checkFinalImei = NormalizeImei(checkFinalResp);
                if (!string.IsNullOrEmpty(checkFinalImei)) break;
                await Task.Delay(1000, ct);
            }
            
            bool matched = (checkFinalImei == expectedImei) && !string.IsNullOrEmpty(checkFinalImei);
            Log($"[{portName}] [IMEI_FINAL] current={checkFinalImei}, expected={expectedImei}, matched={matched.ToString().ToLowerInvariant()}", matched ? "SUCCESS" : "ERROR");

            if (matched)
            {
                if (IsValidImei(normalizedExplicitTarget))
                {
                    saveBackupEntry(new SimBackupEntry
                    {
                        Ccid = ccid,
                        Imei = checkFinalImei,
                        PhoneNumber = port.PhoneNumber,
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        SourceFile = "manual-verified",
                        SimRegDate = port.SimRegDate
                    });
                }
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
        catch (OperationCanceledException)
        {
            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "Phiên xử lý SIM đã bị hủy" };
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
