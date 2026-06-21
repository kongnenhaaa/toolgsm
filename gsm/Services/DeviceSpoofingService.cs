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
        // ===== APPLE iPhone (Dòng mới) =====
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 15 Pro Max",  TacCode = "35437977" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 15 Pro",      TacCode = "35437340" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 15 Plus",     TacCode = "35437651" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 15",          TacCode = "35436980" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 14 Pro Max",  TacCode = "35017482" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 14 Pro",      TacCode = "35287515" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 14 Plus",     TacCode = "35017045" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 14",          TacCode = "35016578" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 13 Pro Max",  TacCode = "35649065" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 13 Pro",      TacCode = "35648589" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 13",          TacCode = "35648168" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 13 mini",     TacCode = "35647590" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 12 Pro Max",  TacCode = "35668411" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 12 Pro",      TacCode = "35667954" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 12",          TacCode = "35314911" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 11 Pro Max",  TacCode = "35384210" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 11 Pro",      TacCode = "35383910" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 11",          TacCode = "35384010" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone SE (2022)",   TacCode = "35324025" },

        // ===== APPLE iPhone (Dòng cũ - 100% HIỆN TÊN TRÊN MỌI WEB CHECK) =====
        new DeviceProfile { Brand = "Apple",   Model = "iPhone XS Max",      TacCode = "35626210" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone X",           TacCode = "35303609" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 8 Plus",      TacCode = "35481709" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 8",           TacCode = "35299809" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 7 Plus",      TacCode = "35383608" },
        new DeviceProfile { Brand = "Apple",   Model = "iPhone 7",           TacCode = "35383008" },

        // ===== SAMSUNG Galaxy (Dòng cũ - 100% HIỆN TÊN TRÊN MỌI WEB CHECK) =====
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S10+",        TacCode = "35411810" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S10",         TacCode = "35411710" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy Note 9",      TacCode = "35830509" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy Note 8",      TacCode = "35850008" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S9+",         TacCode = "35459409" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S8",          TacCode = "35505408" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy J7 Prime",    TacCode = "35315208" },

        // ===== XIAOMI / OPPO (Dòng cũ - 100% HIỆN TÊN) =====
        new DeviceProfile { Brand = "Xiaomi",  Model = "Redmi Note 8",       TacCode = "86427304" },
        new DeviceProfile { Brand = "Xiaomi",  Model = "Redmi Note 7",       TacCode = "86242304" },
        new DeviceProfile { Brand = "Oppo",    Model = "F11 Pro",            TacCode = "86134304" },

        // ===== SAMSUNG Galaxy (Dòng mới) =====
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S24 Ultra",   TacCode = "35583626" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S24+",        TacCode = "35583279" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S24",         TacCode = "35582914" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S23 Ultra",   TacCode = "35105370" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S23+",        TacCode = "35359990" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S23",         TacCode = "35359770" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S22 Ultra",   TacCode = "35465134" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S22+",        TacCode = "35258667" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy S22",         TacCode = "35258334" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy Z Fold 5",    TacCode = "35794580" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy Z Flip 5",    TacCode = "35794215" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy Z Fold 4",    TacCode = "35914619" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy A54",         TacCode = "35636651" },
        new DeviceProfile { Brand = "Samsung", Model = "Galaxy A34",         TacCode = "35636284" },

        // ===== XIAOMI / OPPO / VIVO =====
        new DeviceProfile { Brand = "Xiaomi",  Model = "14 Pro",             TacCode = "86123406" },
        new DeviceProfile { Brand = "Xiaomi",  Model = "14",                 TacCode = "86123006" },
        new DeviceProfile { Brand = "Xiaomi",  Model = "13 Pro",             TacCode = "86877406" },
        new DeviceProfile { Brand = "Xiaomi",  Model = "13",                 TacCode = "86877006" },
        new DeviceProfile { Brand = "Xiaomi",  Model = "Redmi Note 13 Pro",  TacCode = "86543206" },
        new DeviceProfile { Brand = "Xiaomi",  Model = "Redmi Note 12",      TacCode = "86053706" },
        new DeviceProfile { Brand = "Oppo",    Model = "Find X6 Pro",        TacCode = "86234506" },
        new DeviceProfile { Brand = "Oppo",    Model = "Reno 10 Pro",        TacCode = "86121506" },
        new DeviceProfile { Brand = "Vivo",    Model = "X90 Pro",            TacCode = "86345606" },
        new DeviceProfile { Brand = "Vivo",    Model = "V27",                TacCode = "86134806" },

        // ===== GOOGLE Pixel =====
        new DeviceProfile { Brand = "Google",  Model = "Pixel 8 Pro",        TacCode = "35956710" },
        new DeviceProfile { Brand = "Google",  Model = "Pixel 8",            TacCode = "35956345" },
        new DeviceProfile { Brand = "Google",  Model = "Pixel 7 Pro",        TacCode = "35750857" },
        new DeviceProfile { Brand = "Google",  Model = "Pixel 7",            TacCode = "35750489" },
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
