using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using gsm.Models;

namespace gsm.Services;

/// <summary>
/// Quản lý định danh thiết bị ảo (Fake IMEI Spoofing).
/// Dữ liệu được lưu theo CCID của SIM — KHÔNG theo cổng COM.
/// Điều này đảm bảo: cùng 1 SIM cắm vào bất kỳ cổng nào cũng nhận đúng IMEI cũ,
/// tránh để nhà mạng phát hiện SIM thay đổi IMEI liên tục.
/// </summary>
public static class DeviceSpoofingService
{
    private static readonly Random _random = new Random();

    // 🔒 Thread-safe lock
    private static readonly object _identityLock = new object();

    // 📁 File backup riêng (ngoài appsettings.json để dễ di chuyển/backup)
    private static readonly string _backupFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "device_identities.json");

    // 📋 Cache in-memory để tránh đọc file mỗi lần
    private static List<DeviceIdentity>? _cache = null;

    // =========================================================================
    // 📱 DATABASE TAC — Mã nhận dạng dòng máy (8 số đầu IMEI) chuẩn GSMA
    // =========================================================================
    public static readonly List<DeviceProfile> AvailableProfiles = new()
    {
        // ===== APPLE iPhone (verified from GSMA IMEI DB) =====
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 15 Pro Max",  TacCode = "35405527" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 15 Pro",      TacCode = "35405427" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 14 Pro Max",  TacCode = "35292440" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 14 Pro",      TacCode = "35314440" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 14",          TacCode = "35155540" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 13 Pro Max",  TacCode = "35214940" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 13",          TacCode = "35158620" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 12 Pro Max",  TacCode = "35420024" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 12",          TacCode = "35402324" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone SE (2022)",   TacCode = "35259544" },
        // ===== SAMSUNG Galaxy (verified) =====
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S24 Ultra",   TacCode = "35616461" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S23 Ultra",   TacCode = "35105370" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S23",         TacCode = "35359770" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S22 Ultra",   TacCode = "35465134" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S22",         TacCode = "35258334" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy Z Fold 4",    TacCode = "35914619" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy A54",         TacCode = "35636651" },
        // ===== XIAOMI / OPPO / VIVO =====
        new DeviceProfile { Brand = "Xiaomi",  Model = "13 Pro",             TacCode = "86773106" },
        new DeviceProfile { Brand = "Xiaomi",  Model = "Redmi Note 12",      TacCode = "86534906" },
        new DeviceProfile { Brand = "Oppo",    Model = "Reno 10 Pro",        TacCode = "86820606" },
        new DeviceProfile { Brand = "Vivo",    Model = "V27",                TacCode = "86773006" },
        // ===== GOOGLE Pixel =====
        new DeviceProfile { Brand = "Google",  Model = "Pixel 8 Pro",        TacCode = "35956710" },
        new DeviceProfile { Brand = "Google",  Model = "Pixel 7 Pro",        TacCode = "35750857" },
    };

    // =========================================================================
    // 🔑 Sinh IMEI hợp lệ
    // =========================================================================

    public static DeviceProfile GetRandomProfile()
        => AvailableProfiles[_random.Next(AvailableProfiles.Count)];

    /// <summary>
    /// Sinh IMEI 15 số hợp lệ: 8 TAC + 6 Serial ngẫu nhiên + 1 Luhn checksum.
    /// Verified: test case "490154203237518" → checkdigit=8 ✅
    /// </summary>
    public static string GenerateImei(string tac)
    {
        if (string.IsNullOrWhiteSpace(tac) || tac.Length != 8)
            throw new ArgumentException("TAC phải gồm chính xác 8 chữ số.");

        // Tạo serial ngẫu nhiên 6 chữ số (100000..999998)
        string partialImei = tac + _random.Next(100000, 999999).ToString();
        return partialImei + CalculateLuhnDigit(partialImei).ToString();
    }

    /// <summary>
    /// Luhn Checksum chuẩn GSMA — duyệt phải sang trái, double vị trí ODD từ phải.
    /// Verified với test case Wikipedia "490154203237518".
    /// </summary>
    private static int CalculateLuhnDigit(string imei14)
    {
        int sum = 0;
        int length = imei14.Length;
        for (int i = length - 1; i >= 0; i--)
        {
            int digit = imei14[i] - '0';
            if ((length - i) % 2 == 1) // vị trí lẻ từ phải → double
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        return (10 - (sum % 10)) % 10;
    }

    // =========================================================================
    // 💾 Persistence — đọc/ghi file device_identities.json
    // =========================================================================

    private static List<DeviceIdentity> LoadAll()
    {
        if (_cache != null) return _cache;
        lock (_identityLock)
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(_backupFilePath))
                {
                    var json = File.ReadAllText(_backupFilePath);
                    _cache = JsonSerializer.Deserialize<List<DeviceIdentity>>(json) ?? new List<DeviceIdentity>();
                    return _cache;
                }
            }
            catch { /* file lỗi → tạo mới */ }
            _cache = new List<DeviceIdentity>();
            return _cache;
        }
    }

    private static void SaveAll(List<DeviceIdentity> list)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_backupFilePath, JsonSerializer.Serialize(list, options));
        }
        catch { /* ghi file lỗi → bỏ qua, tránh crash app */ }
    }

    // =========================================================================
    // 🔍 Lookup & Create — theo CCID của SIM (KHÔNG theo cổng COM)
    // =========================================================================

    /// <summary>
    /// Lấy định danh đã lưu theo CCID, hoặc tạo mới nếu đây là SIM chưa từng gặp.
    /// Cập nhật LastPortName để tracking.
    /// Thread-safe với double-check lock pattern.
    /// </summary>
    public static DeviceIdentity GetOrCreateByCcid(string ccid, string portName, string phoneNumber = "")
    {
        if (string.IsNullOrWhiteSpace(ccid))
            throw new ArgumentException("CCID không được trống.");

        var list = LoadAll();

        // Fast path: đã tồn tại
        var existing = list.FirstOrDefault(d => d.Ccid == ccid);
        if (existing != null)
        {
            // Cập nhật port hiện tại và SĐT (nếu có)
            if (existing.LastPortName != portName || (!string.IsNullOrEmpty(phoneNumber) && existing.PhoneNumber != phoneNumber))
            {
                lock (_identityLock)
                {
                    existing.LastPortName = portName;
                    if (!string.IsNullOrEmpty(phoneNumber)) existing.PhoneNumber = phoneNumber;
                    SaveAll(list);
                }
            }
            return existing;
        }

        lock (_identityLock)
        {
            // Double-check inside lock
            existing = list.FirstOrDefault(d => d.Ccid == ccid);
            if (existing != null)
            {
                existing.LastPortName = portName;
                if (!string.IsNullOrEmpty(phoneNumber)) existing.PhoneNumber = phoneNumber;
                SaveAll(list);
                return existing;
            }

            // Tạo mới — chọn random profile
            var profile = GetRandomProfile();
            var newIdentity = new DeviceIdentity
            {
                Ccid         = ccid,
                LastPortName = portName,
                PhoneNumber  = phoneNumber,
                Brand        = profile.Brand,
                Model        = profile.Model,
                DeviceName   = profile.DisplayName,
                AssignedImei = GenerateImei(profile.TacCode),
                CreatedAt    = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            list.Add(newIdentity);
            SaveAll(list);
            return newIdentity;
        }
    }

    /// <summary>
    /// Xác nhận IMEI đã được ghi thành công vào chip — cập nhật timestamp.
    /// </summary>
    public static void MarkApplied(string ccid)
    {
        var list = LoadAll();
        lock (_identityLock)
        {
            var entry = list.FirstOrDefault(d => d.Ccid == ccid);
            if (entry != null)
            {
                entry.LastAppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveAll(list);
            }
        }
    }

    /// <summary>
    /// Reset định danh của một SIM cụ thể (theo CCID) — tạo IMEI mới.
    /// </summary>
    public static DeviceIdentity RecreateByCcid(string ccid, string portName)
    {
        var list = LoadAll();
        lock (_identityLock)
        {
            var existing = list.FirstOrDefault(d => d.Ccid == ccid);
            if (existing != null)
            {
                list.Remove(existing);
                SaveAll(list);
                _cache = list;
            }
        }
        return GetOrCreateByCcid(ccid, portName);
    }

    /// <summary>
    /// Reset định danh của một cổng COM (dùng từ UI khi chưa biết CCID).
    /// </summary>
    public static DeviceIdentity RecreateByPort(string portName)
    {
        var list = LoadAll();
        lock (_identityLock)
        {
            var existing = list.FirstOrDefault(d => d.LastPortName == portName);
            if (existing != null)
            {
                list.Remove(existing);
                SaveAll(list);
                _cache = list;
                return GetOrCreateByCcid(existing.Ccid, portName);
            }
        }
        // Không có CCID → không thể tạo lại đúng cách
        throw new InvalidOperationException($"Không tìm thấy định danh cho cổng {portName}.");
    }

    /// <summary>
    /// Xóa toàn bộ định danh (Reset All).
    /// </summary>
    public static void ClearAll()
    {
        lock (_identityLock)
        {
            _cache = new List<DeviceIdentity>();
            SaveAll(_cache);
        }
    }

    /// <summary>
    /// Lấy toàn bộ danh sách để hiển thị trên UI.
    /// </summary>
    public static List<DeviceIdentity> GetAll() => new List<DeviceIdentity>(LoadAll());

    /// <summary>
    /// Đường dẫn file backup để user có thể sao chép.
    /// </summary>
    public static string BackupFilePath => _backupFilePath;
}
