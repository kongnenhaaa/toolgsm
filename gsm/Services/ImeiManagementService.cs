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
    public string TargetSource { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool ModemResetRequested { get; set; }
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

    public static bool IsValidImei(string? imei)
    {
        string clean = NormalizeImeiValue(imei);
        if (clean.Length != 15) return false;

        int sum = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            int digit = clean[i] - '0';
            if ((i & 1) != 0)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        return sum % 10 == 0;
    }

    /// <summary>
    /// So sánh danh tính thiết bị theo TAC + SNR (14 số). Theo 3GPP, check digit
    /// không được truyền lên mạng và vị trí cuối có thể được biểu diễn bằng spare digit 0.
    /// Chỉ chấp nhận khác biệt này khi một phía là IMEI Luhn hợp lệ.
    /// </summary>
    public static bool AreEquivalentImei(string? left, string? right)
    {
        string a = NormalizeImeiValue(left);
        string b = NormalizeImeiValue(right);
        if (a.Length != 15 || b.Length != 15) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        if (!a.AsSpan(0, 14).SequenceEqual(b.AsSpan(0, 14))) return false;

        return (IsValidImei(a) && b[14] == '0')
            || (IsValidImei(b) && a[14] == '0');
    }

    public static bool IsUsableObservedImei(string? imei)
    {
        string clean = NormalizeImeiValue(imei);
        if (IsValidImei(clean)) return true;
        if (clean.Length != 15 || clean[14] != '0') return false;

        string canonical = clean[..14] + CalculateCheckDigit(clean[..14]);
        return IsValidImei(canonical);
    }

    public static string ToCanonicalImei(string? imei)
    {
        string clean = NormalizeImeiValue(imei);
        if (IsValidImei(clean)) return clean;
        if (clean.Length == 15 && clean[14] == '0')
        {
            int checkDigit = CalculateCheckDigit(clean[..14]);
            if (checkDigit >= 0) return clean[..14] + checkDigit;
        }
        return clean;
    }

    public static bool TryNormalizeBackupImei(string? imei, out string canonicalImei)
    {
        canonicalImei = ToCanonicalImei(imei);
        if (IsValidImei(canonicalImei)) return true;
        canonicalImei = string.Empty;
        return false;
    }

    internal static bool StoredImeiMatchesOrUnavailable(string? response, string expectedImei)
    {
        if (string.IsNullOrWhiteSpace(response)
            || response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || response.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return false;

        string storedImei = NormalizeImeiValue(response);
        // Đường đăng ký mạng phải fail-closed: "OK" không kèm giá trị, timeout và
        // thanh ghi không đọc được đều không phải bằng chứng rằng NV đã chứa IMEI mới.
        return storedImei.Length == 15 && AreEquivalentImei(storedImei, expectedImei);
    }

    private static int CalculateCheckDigit(string first14Digits)
    {
        if (first14Digits.Length != 14 || first14Digits.Any(c => !char.IsDigit(c))) return -1;
        int sum = 0;
        for (int i = 0; i < first14Digits.Length; i++)
        {
            int digit = first14Digits[i] - '0';
            if ((i & 1) != 0)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        return (10 - (sum % 10)) % 10;
    }

    private static string NormalizeImeiValue(string? imei) => string.IsNullOrWhiteSpace(imei)
        ? string.Empty
        : new string(imei.Where(char.IsDigit).ToArray());

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
        string? explicitTargetImei = null,
        bool overwriteBackupWithCurrentImei = false)
    {
        string portName = port.PortName;
        try
        {
            async Task<bool> SessionIsValidAsync()
            {
                ct.ThrowIfCancellationRequested();
                return validateIdentityAsync == null || await validateIdentityAsync();
            }

            static string ExtractImei(string? response)
            {
                if (string.IsNullOrWhiteSpace(response)) return string.Empty;
                return System.Text.RegularExpressions.Regex.Match(
                    response, @"(?<!\d)\d{15}(?!\d)").Value;
            }

            static bool CommandSucceeded(string? response) =>
                !string.IsNullOrWhiteSpace(response)
                && response.Contains("OK", StringComparison.OrdinalIgnoreCase)
                && !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                && !response.Contains("Timeout", StringComparison.OrdinalIgnoreCase);

            if (!await SessionIsValidAsync())
                return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã thay đổi trong lúc xử lý" };

            SimBackupEntry? cachedEntry = getBackupEntry(ccid);
            string canonicalBackupImei = string.Empty;
            bool hasValidBackup = cachedEntry != null
                && TryNormalizeBackupImei(cachedEntry.Imei, out canonicalBackupImei);
            string canonicalCurrentImei = ToCanonicalImei(currentImei);
            bool currentImeiValid = IsValidImei(canonicalCurrentImei);

            if (!hasValidBackup && !forceAccept)
            {
                if (settings.EnableNewSimIntakeMode)
                {
                    return new ImeiProcessResult
                    {
                        Status = ImeiProcessStatus.WaitingAccept,
                        ErrorMessage = "SIM mới chưa trong hệ thống, đang bị chặn chờ thao tác IMEI",
                        FinalImei = canonicalCurrentImei
                    };
                }
                if (settings.BlockUnknownSims)
                    return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = "SIM mới chưa được chấp thuận" };
            }

            string targetImei = string.Empty;
            string targetSource = string.Empty;
            string explicitImei = NormalizeImei(explicitTargetImei);
            bool explicitWriteRequested = IsValidImei(explicitImei);
            if (explicitWriteRequested)
            {
                if (imeiAlreadyAssigned?.Invoke(explicitImei) == true)
                    return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "IMEI mục tiêu đang được gán cho SIM khác" };
                targetImei = explicitImei;
                targetSource = "manual-verified";
            }
            else if (settings.EnableImeiRestore && hasValidBackup)
            {
                targetImei = canonicalBackupImei;
                targetSource = string.IsNullOrWhiteSpace(cachedEntry!.SourceFile)
                    ? "imei_backup.xlsx"
                    : cachedEntry.SourceFile;
                dispatcherInvoke(() =>
                {
                    port.DeviceName = GetDeviceNameFromImei(targetImei);
                    if (!string.IsNullOrWhiteSpace(cachedEntry.PhoneNumber)) port.PhoneNumber = cachedEntry.PhoneNumber;
                    port.CreatedAt = cachedEntry.CreatedAt;
                });
            }
            else if (forceAccept || !settings.EnableNewSimIntakeMode)
            {
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    string candidate = GenerateRandomImei();
                    if (imeiAlreadyAssigned?.Invoke(candidate) != true)
                    {
                        targetImei = candidate;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(targetImei))
                    return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "Không tạo được IMEI duy nhất" };
                targetSource = "auto-generation";
            }
            else
            {
                targetImei = canonicalCurrentImei;
                targetSource = "current-modem";
            }

            if (!IsValidImei(targetImei))
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.WrongImei };

            if (!explicitWriteRequested
                && string.Equals(canonicalCurrentImei, targetImei, StringComparison.Ordinal))
            {
                dispatcherInvoke(() => port.Imei = targetImei);
                return new ImeiProcessResult
                {
                    Status = ImeiProcessStatus.Matched,
                    FinalImei = targetImei,
                    TargetSource = targetSource
                };
            }

            if (!await SessionIsValidAsync())
                return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã thay đổi trước khi ghi IMEI" };

            // Persist the original value before the first write. The cache callback is
            // first-write-wins, so a generated IMEI can never replace this XLSX value.
            if ((overwriteBackupWithCurrentImei || !hasValidBackup) && currentImeiValid)
            {
                var originalEntry = new SimBackupEntry
                {
                    Ccid = ccid,
                    Imei = canonicalCurrentImei,
                    PhoneNumber = port.PhoneNumber,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    SourceFile = "imei_backup.xlsx",
                    SimRegDate = port.SimRegDate
                };
                saveBackupEntry(originalEntry);
                SimBackupEntry? persisted = getBackupEntry(ccid);
                if (persisted == null
                    || !TryNormalizeBackupImei(persisted.Imei, out string persistedImei)
                    || !string.Equals(persistedImei, canonicalCurrentImei, StringComparison.Ordinal))
                {
                    return new ImeiProcessResult
                    {
                        Status = ImeiProcessStatus.SecurityBlocked,
                        ErrorMessage = "Không xác nhận được IMEI gốc trong imei_backup.xlsx; đã hủy ghi"
                    };
                }
                Log($"[{portName}] [IMEI_BACKUP_SAVED] CCID={ccid}; previous={canonicalCurrentImei}; overwrite={overwriteBackupWithCurrentImei}; file=imei_backup.xlsx", "SUCCESS");
            }

            Log($"[{portName}] [IMEI_TARGET] source={targetSource}; CCID={ccid}; target={targetImei}");

            // Sequence captured from SAuto: CFUN=4, verify, write slot 7,
            // wait 500 ms, read slot 7, wait 100 ms, then reset with CFUN=1,1.
            string cfun4 = await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 10000, silent: true);
            string cfunState = await _modemService.SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true);
            if (!CommandSucceeded(cfun4)
                || !System.Text.RegularExpressions.Regex.IsMatch(cfunState, @"\+CFUN:\s*4\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.RadioOffFailed };
            }

            string write = await _modemService.SendCommandAsync(portName, $"AT+EGMR=1,7,\"{targetImei}\"", 30000, silent: true);
            if (!CommandSucceeded(write))
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = "Ghi IMEI slot 7 thất bại" };

            await Task.Delay(500, ct);
            string storedAfter = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,7;", 10000, silent: true);
            string verifiedImei = ExtractImei(storedAfter);
            if (!string.Equals(verifiedImei, targetImei, StringComparison.Ordinal))
            {
                await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true);
                Log($"[{portName}] [IMEI_WRITE_VERIFY_FAILED] read={verifiedImei}; expected={targetImei}", "ERROR");
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.WrongImei };
            }

            if (!await SessionIsValidAsync())
                return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã thay đổi sau khi ghi IMEI" };

            await Task.Delay(100, ct);
            dispatcherInvoke(() =>
            {
                port.Imei = targetImei;
                port.IsRebooting = true;
            });
            string reset = await _modemService.SendCommandAsync(portName, "AT+CFUN=1,1", 10000, silent: true);
            if (reset.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "Modem từ chối CFUN=1,1 sau khi ghi IMEI" };

            Log($"[{portName}] [IMEI_WRITE_OK] slot7={targetImei}; modem reset requested", "SUCCESS");
            return new ImeiProcessResult
            {
                Status = ImeiProcessStatus.Applied,
                FinalImei = targetImei,
                TargetSource = targetSource,
                ModemResetRequested = true
            };
        }
        catch (OperationCanceledException)
        {
            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "Phiên xử lý SIM đã bị hủy" };
        }
        catch (Exception ex)
        {
            Log($"[{portName}] Lỗi trong quá trình xử lý IMEI: {ex.Message}", "ERROR");
            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// SAuto-compatible Create/Restore path when the modem has no SIM/CCID.
    /// IMEI lives in modem NV, so slot 7 can be read and written while CFUN=4.
    /// </summary>
    public async Task<ImeiProcessResult> ProcessImeiWithoutSimAsync(
        SimPort port,
        string explicitTargetImei,
        Func<string, bool>? savePreviousImei,
        Action<Action> dispatcherInvoke,
        bool backupCurrentBeforeWrite,
        CancellationToken ct = default)
    {
        string portName = port.PortName;
        string targetImei = NormalizeImei(explicitTargetImei);

        static string ExtractImei(string? response) => string.IsNullOrWhiteSpace(response)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Match(response, @"(?<!\d)\d{15}(?!\d)").Value;

        static bool CommandSucceeded(string? response) =>
            !string.IsNullOrWhiteSpace(response)
            && response.Contains("OK", StringComparison.OrdinalIgnoreCase)
            && !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            && !response.Contains("Timeout", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (!IsValidImei(targetImei))
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.WrongImei };

            string cfun4 = await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 10000, silent: true, ct);
            string cfunState = await _modemService.SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true, ct);
            if (!CommandSucceeded(cfun4)
                || !System.Text.RegularExpressions.Regex.IsMatch(cfunState, @"\+CFUN:\s*4\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.RadioOffFailed };
            }

            string currentResponse = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,7;", 10000, silent: true, ct);
            string currentImei = ExtractImei(currentResponse);
            if (!IsValidImei(currentImei))
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = "Không đọc được IMEI hiện tại trước khi ghi" };

            if (backupCurrentBeforeWrite && (savePreviousImei == null || !savePreviousImei(currentImei)))
            {
                return new ImeiProcessResult
                {
                    Status = ImeiProcessStatus.SecurityBlocked,
                    ErrorMessage = "Không lưu/xác minh được IMEI hiện tại trong imei_backup.xlsx; đã hủy ghi"
                };
            }

            Log($"[{portName}] [IMEI_TARGET_NO_SIM] current={currentImei}; target={targetImei}; backup={backupCurrentBeforeWrite}");
            string write = await _modemService.SendCommandAsync(portName, $"AT+EGMR=1,7,\"{targetImei}\"", 30000, silent: true, ct);
            if (!CommandSucceeded(write))
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = "Ghi IMEI slot 7 thất bại" };

            await Task.Delay(500, ct);
            string storedAfter = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,7;", 10000, silent: true, ct);
            string verifiedImei = ExtractImei(storedAfter);
            if (!string.Equals(verifiedImei, targetImei, StringComparison.Ordinal))
            {
                await _modemService.SendCommandAsync(portName, "AT+CFUN=4", 5000, silent: true, ct);
                return new ImeiProcessResult { Status = ImeiProcessStatus.SecurityBlocked, ErrorMessage = SecurityErrors.WrongImei };
            }

            await Task.Delay(100, ct);
            dispatcherInvoke(() =>
            {
                port.Imei = targetImei;
                port.IsRebooting = true;
            });
            string reset = await _modemService.SendCommandAsync(portName, "AT+CFUN=1,1", 10000, silent: true, ct);
            if (reset.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "Modem từ chối CFUN=1,1 sau khi ghi IMEI" };

            Log($"[{portName}] [IMEI_WRITE_NO_SIM_OK] previous={currentImei}; slot7={targetImei}; modem reset requested", "SUCCESS");
            return new ImeiProcessResult
            {
                Status = ImeiProcessStatus.Applied,
                FinalImei = targetImei,
                TargetSource = backupCurrentBeforeWrite ? "new-random-no-sim" : "modem-backup-xlsx",
                ModemResetRequested = true
            };
        }
        catch (OperationCanceledException)
        {
            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "Tác vụ IMEI không SIM đã bị hủy" };
        }
        catch (Exception ex)
        {
            Log($"[{portName}] Lỗi xử lý IMEI không SIM: {ex.Message}", "ERROR");
            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = ex.Message };
        }
    }

    private async Task<ImeiProcessResult> ProcessImeiLegacyAsync(
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
            string canonicalBackupImei = string.Empty;
            bool hasValidBackup = cachedEntry != null
                && TryNormalizeBackupImei(cachedEntry.Imei, out canonicalBackupImei);
            bool isHardwareImeiValid = IsUsableObservedImei(NormalizeImei(currentImei));
            string canonicalCurrentImei = ToCanonicalImei(currentImei);

            if (hasValidBackup
                && !string.Equals(cachedEntry!.Imei, canonicalBackupImei, StringComparison.Ordinal))
            {
                // File cũ có thể lưu dạng network spare digit 0. Nâng cấp về
                // IMEI Luhn 15 số trước khi ghi modem và lưu lại workbook.
                cachedEntry.Imei = canonicalBackupImei;
                saveBackupEntry(cachedEntry);
                Log($"[{portName}] [IMEI_BACKUP_NORMALIZED] CCID={ccid} IMEI={canonicalBackupImei}", "SUCCESS");
            }

            // Quyết định xem SIM này có cần bị chặn chờ thao tác IMEI hay không.
            bool treatAsNewSim = (cachedEntry == null) || (!isHardwareImeiValid && !hasValidBackup);

            // 1. Kiểm tra chặn/chờ duyệt đối với SIM mới
            if (treatAsNewSim && !forceAccept)
            {
                if (settings.EnableNewSimIntakeMode)
                {
                    // Giữ RF tắt và chặn SIM cho tới khi người dùng chọn Tạo mới hoặc Khôi phục.
                    return new ImeiProcessResult
                    {
                        Status = ImeiProcessStatus.WaitingAccept,
                        ErrorMessage = "SIM mới chưa trong hệ thống, đang bị chặn chờ thao tác IMEI",
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
                SimBackupEntry validCachedEntry = cachedEntry!;
                string candidateImei = canonicalBackupImei;
                targetImei = candidateImei;
                targetSource = string.IsNullOrWhiteSpace(validCachedEntry.SourceFile) ? "imei_backup.xlsx" : validCachedEntry.SourceFile;
                
                dispatcherInvoke(() =>
                {
                    port.DeviceName = GetDeviceNameFromImei(candidateImei);
                    if (!string.IsNullOrWhiteSpace(validCachedEntry.PhoneNumber))
                    {
                        port.PhoneNumber = validCachedEntry.PhoneNumber;
                    }
                    port.CreatedAt = validCachedEntry.CreatedAt;
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
                        Imei = canonicalCurrentImei,
                        PhoneNumber = port.PhoneNumber,
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        SourceFile = "auto-learn",
                        SimRegDate = port.SimRegDate
                    };
                    saveBackupEntry(newEntry);
                    
                    Log($"[{portName}] Cắm lần đầu, tự động ghi nhận IMEI gốc hợp lệ: {canonicalCurrentImei} vào file backup.", "SUCCESS");

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
                targetImei = canonicalCurrentImei;
            }

            expectedImei = targetImei;

            // 5. Chỉ coi là đã sẵn sàng khi CGSN và cả hai thanh ghi NV đều khớp.
            bool targetAlreadyPresent = AreEquivalentImei(targetImei, currentImei);
            bool storedTargetsPresent = false;
            if (targetAlreadyPresent && !string.IsNullOrEmpty(targetImei))
            {
                string preStored7 = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,7", 10000, silent: true);
                string preStored10 = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,10", 10000, silent: true);
                storedTargetsPresent = StoredImeiMatchesOrUnavailable(preStored7, targetImei)
                    && StoredImeiMatchesOrUnavailable(preStored10, targetImei);
            }
            bool writeRequired = !string.IsNullOrEmpty(targetImei)
                && (!targetAlreadyPresent || !storedTargetsPresent);
            if (targetAlreadyPresent
                && !string.Equals(targetImei, currentImei, StringComparison.Ordinal)
                && storedTargetsPresent)
            {
                Log($"[{portName}] [IMEI_EQUIVALENT] modem={currentImei}; backup={targetImei}; cùng TAC+SNR 14 số, chỉ khác Check Digit/Spare Digit. Không tráng lại.", "SUCCESS");
            }
            if (writeRequired)
            {
                Log($"[{portName}] [IMEI_TARGET] source={targetSource} CCID={ccid} target_imei={targetImei}");
                Log($"[{portName}] [IMEI_CHANGE] CGSN hoặc NV slot chưa khớp mục tiêu {targetImei}. Bắt đầu ghi đồng bộ hai slot...", "WARNING");
                
                string cfun0 = await _modemService.SendCommandAsync(portName, "AT+CFUN=0", 10000, silent: true);
                string cfun0State = await _modemService.SendCommandAsync(portName, "AT+CFUN?", 5000, silent: true);
                if (!cfun0.Contains("OK")
                    || !System.Text.RegularExpressions.Regex.IsMatch(
                        cfun0State, @"\+CFUN:\s*0\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    Log($"[{portName}] Không xác nhận được CFUN=0 trước khi ghi IMEI. Hủy ghi.", "ERROR");
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
                    bool slot2Written = false;
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

                    if (!isDisconnected && !isUnsupported)
                    {
                        // Ghi thêm vào IMEI slot 2 (slot 10) cùng giá trị để modem không
                        // broadcast IMEI cũ lên mạng khi đăng ký với BTS.
                        // EC20F/EC20CEHCLGR cần delay đủ lớn sau EGMR=1,7 trước khi ghi
                        // slot 10 và verify — AT+CGSN trả về rỗng nếu gọi quá sớm.
                        // Dùng CancellationToken.None để delay không bị cắt khi session invalidate.
                        await Task.Delay(1500, CancellationToken.None);
                        string write2Resp = await _modemService.SendCommandAsync(portName, $"AT+EGMR=1,10,\"{targetImei}\"", 10000);
                        if (IsConnectionError(write2Resp))
                        {
                            isDisconnected = true;
                        }
                        else if (write2Resp.Contains("ERROR"))
                        {
                            Log($"[{portName}] [IMEI2_WRITE_FAILED] Slot 10 không ghi được; không cho phép bật RF: {write2Resp.Trim()}", "ERROR");
                        }
                        else
                        {
                            slot2Written = true;
                            Log($"[{portName}] [IMEI2_WRITTEN] Đã ghi IMEI slot 10 thành công: {targetImei}", "INFO");
                        }
                        // Delay sau slot 10 để NV được flush trước khi đọc lại verify
                        if (!isDisconnected) await Task.Delay(1000, CancellationToken.None);
                    }
                    else if (!isDisconnected)
                    {
                        // EGMR thất bại nhưng chưa disconnect — vẫn cần delay để modem ổn định
                        await Task.Delay(1000, CancellationToken.None);
                    }

                    string finalImeiResp = await _modemService.SendCommandAsync(portName, "AT+CGSN", 10000);
                    string finalImei = NormalizeImei(finalImeiResp);
                    string storedImeiResp = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,7", 10000);
                    string storedImei = NormalizeImei(storedImeiResp);
                    bool storedRegisterMatches = StoredImeiMatchesOrUnavailable(storedImeiResp, targetImei);

                    // Đọc và xác minh IMEI slot 2 (slot 10) — slot mà nhà mạng cũng đọc khi thiết bị đăng ký mạng.
                    string stored2ImeiResp = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,10", 10000);
                    string stored2Imei = NormalizeImei(stored2ImeiResp);
                    bool stored2RegisterMatches = slot2Written
                        && StoredImeiMatchesOrUnavailable(stored2ImeiResp, targetImei);

                    Log($"[{portName}] [IMEI_WRITE_VERIFY] CGSN={finalImei}; EGMR_slot7={storedImei}; EGMR_slot10={stored2Imei}; expected={targetImei}");

                    if (AreEquivalentImei(finalImei, targetImei) && storedRegisterMatches && stored2RegisterMatches)
                    {
                        if (!await SessionIsValidAsync())
                            return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã thay đổi sau khi ghi IMEI" };

                        success = true;
                        dispatcherInvoke(() => port.Imei = targetImei);
                        Log($"[{portName}] Ghi đè IMEI thành công ở lần thử {attempt}: {targetImei} (slot7 ✓, slot10 ✓)", "SUCCESS");

                        // Giữ radio ở CFUN=0. Caller phải xác minh lại CCID/IMEI rồi mới
                        // được bật CFUN=1; tránh SIM mới lên mạng trong cửa sổ chưa xác thực.
                        Log($"[{portName}] IMEI đã ghi và xác minh cả 2 slot; tiếp tục giữ radio tắt chờ xác minh CCID cuối.", "INFO");
                        
                        // Caller tiếp tục qua cổng xác minh và cấu hình offline trước khi bật radio.
                        return new ImeiProcessResult
                        {
                            Status = ImeiProcessStatus.Applied,
                            FinalImei = targetImei,
                            TargetSource = targetSource
                        };
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
                            Log($"[{portName}] Ghi đè IMEI thất bại ở lần thử {attempt} (CGSN={finalImei}; EGMR_slot7={storedImei}; EGMR_slot10={stored2Imei}). Giữ sóng tắt.", "ERROR");
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
                    if (AreEquivalentImei(targetImei, currentImei) && !string.IsNullOrEmpty(currentImei))
                    {
                        Log($"[{portName}] IMEI khớp với mục tiêu: {currentImei}", "SUCCESS");
                    }
                    // QUAN TRỌNG: Không gọi AT+CFUN=1 ở đây.
                    // CompletePortInitializationAsync sẽ cấu hình offline, xác minh lại danh tính
                    // rồi mới bật CFUN=1. Không được bật radio trực tiếp tại service này.
                }

            string checkFinalImei = string.Empty;
            string checkStoredImei = string.Empty;
            string checkStoredResp = string.Empty;
            string checkStored2Resp = string.Empty;
            string checkStored2Imei = string.Empty;
            for (int i = 0; i < 3; i++)
            {
                if (!await SessionIsValidAsync())
                    return new ImeiProcessResult { Status = ImeiProcessStatus.Error, ErrorMessage = "SIM đã thay đổi trước bước xác minh cuối" };
                string checkFinalResp = await _modemService.SendCommandAsync(portName, "AT+CGSN", 10000, silent: true);
                checkFinalImei = NormalizeImei(checkFinalResp);
                checkStoredResp = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,7", 10000, silent: true);
                checkStoredImei = NormalizeImei(checkStoredResp);
                // Xác minh IMEI slot 2 — thanh ghi nhà mạng cũng đọc khi thiết bị đăng ký BTS
                checkStored2Resp = await _modemService.SendCommandAsync(portName, "AT+EGMR=0,10", 10000, silent: true);
                checkStored2Imei = NormalizeImei(checkStored2Resp);
                if (!string.IsNullOrEmpty(checkFinalImei)) break;
                await Task.Delay(1000, ct);
            }
            
            bool storedFinalMatches  = StoredImeiMatchesOrUnavailable(checkStoredResp, expectedImei);
            bool stored2FinalMatches = StoredImeiMatchesOrUnavailable(checkStored2Resp, expectedImei);
            bool matched = AreEquivalentImei(checkFinalImei, expectedImei) && storedFinalMatches && stored2FinalMatches;
            Log($"[{portName}] [IMEI_FINAL] CGSN={checkFinalImei}, EGMR_slot7={checkStoredImei}, EGMR_slot10={checkStored2Imei}, expected={expectedImei}, matched={matched.ToString().ToLowerInvariant()}", matched ? "SUCCESS" : "ERROR");

            if (matched)
            {
                bool wasApplied = !string.IsNullOrEmpty(targetImei) && !targetAlreadyPresent;
                return new ImeiProcessResult 
                { 
                    Status = wasApplied ? ImeiProcessStatus.Applied : ImeiProcessStatus.Matched, 
                    FinalImei = expectedImei,
                    TargetSource = targetSource
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
