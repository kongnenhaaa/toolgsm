using System.IO;
using System.Text.Json;

namespace gsm.Services;

/// <summary>
/// Keeps the short CBSS/OneBSS account picker history requested for the
/// DKTTTB tab. OTPs and session tokens are never written here.
/// </summary>
public sealed class DeviceUnlockAccountHistoryService
{
    private const int MaximumAccountsPerSource = 10;
    internal const string FileName = "device_unlock_account_history.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _filePath;
    private DeviceUnlockAccountHistory _history;

    public DeviceUnlockAccountHistoryService()
        : this(AppPaths.ForUserDataFile(FileName))
    {
    }

    internal DeviceUnlockAccountHistoryService(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        _history = Load();
    }

    public IReadOnlyList<string> GetCbssAccounts() => GetAccounts(cbss: true);

    public IReadOnlyList<string> GetEmployAccounts() => GetAccounts(cbss: false);

    public void RememberCbss(string username) => Remember(username, cbss: true);

    public void RememberCbss(string username, string password) =>
        Remember(username, password, cbss: true);

    public void RememberEmploy(string username) => Remember(username, cbss: false);

    public void RememberEmploy(string username, string password) =>
        Remember(username, password, cbss: false);

    public string GetCbssPassword(string username) => GetPassword(username, cbss: true);

    public string GetEmployPassword(string username) => GetPassword(username, cbss: false);

    public void ClearCbssAccounts() => Clear(cbss: true);

    public void ClearEmployAccounts() => Clear(cbss: false);

    private IReadOnlyList<string> GetAccounts(bool cbss)
    {
        lock (_sync)
            return (cbss ? _history.CbssAccounts : _history.EmployAccounts).ToArray();
    }

    private void Remember(string username, bool cbss) =>
        Remember(username, password: null, cbss);

    private void Remember(string username, string? password, bool cbss)
    {
        string normalized = username.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return;

        lock (_sync)
        {
            List<string> accounts = cbss ? _history.CbssAccounts : _history.EmployAccounts;
            Dictionary<string, string> passwords = cbss
                ? _history.CbssPasswords
                : _history.EmployPasswords;
            accounts.RemoveAll(account =>
                string.Equals(account, normalized, StringComparison.OrdinalIgnoreCase));
            accounts.Insert(0, normalized);
            if (accounts.Count > MaximumAccountsPerSource)
                accounts.RemoveRange(MaximumAccountsPerSource, accounts.Count - MaximumAccountsPerSource);

            if (password is not null)
                passwords[normalized] = password;

            foreach (string staleUsername in passwords.Keys
                         .Where(saved => !accounts.Contains(saved, StringComparer.OrdinalIgnoreCase))
                         .ToArray())
            {
                passwords.Remove(staleUsername);
            }

            Persist();
        }
    }

    private string GetPassword(string username, bool cbss)
    {
        string normalized = username.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        lock (_sync)
        {
            Dictionary<string, string> passwords = cbss
                ? _history.CbssPasswords
                : _history.EmployPasswords;
            return passwords.TryGetValue(normalized, out string? password)
                ? password
                : string.Empty;
        }
    }

    private void Clear(bool cbss)
    {
        lock (_sync)
        {
            List<string> accounts = cbss ? _history.CbssAccounts : _history.EmployAccounts;
            Dictionary<string, string> passwords = cbss
                ? _history.CbssPasswords
                : _history.EmployPasswords;
            if (accounts.Count == 0 && passwords.Count == 0) return;
            accounts.Clear();
            passwords.Clear();
            Persist();
        }
    }

    private DeviceUnlockAccountHistory Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new DeviceUnlockAccountHistory();
            string json = File.ReadAllText(_filePath);
            DeviceUnlockAccountHistory? saved =
                JsonSerializer.Deserialize<DeviceUnlockAccountHistory>(json);
            return Normalize(saved ?? new DeviceUnlockAccountHistory());
        }
        catch
        {
            return new DeviceUnlockAccountHistory();
        }
    }

    private void Persist()
    {
        string temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            string json = JsonSerializer.Serialize(_history, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch
        {
            // Optional picker history must never stop a login.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static DeviceUnlockAccountHistory Normalize(DeviceUnlockAccountHistory history) => new()
    {
        CbssAccounts = NormalizeAccounts(history.CbssAccounts),
        EmployAccounts = NormalizeAccounts(history.EmployAccounts),
        CbssPasswords = NormalizePasswords(history.CbssPasswords, history.CbssAccounts),
        EmployPasswords = NormalizePasswords(history.EmployPasswords, history.EmployAccounts)
    };

    private static List<string> NormalizeAccounts(IEnumerable<string>? accounts) =>
        (accounts ?? [])
        .Where(account => !string.IsNullOrWhiteSpace(account))
        .Select(account => account.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaximumAccountsPerSource)
        .ToList();

    private static Dictionary<string, string> NormalizePasswords(
        IReadOnlyDictionary<string, string>? passwords,
        IEnumerable<string>? accounts)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (passwords is null) return normalized;

        HashSet<string> activeAccounts = NormalizeAccounts(accounts)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string username, string password) in passwords)
        {
            string normalizedUsername = username.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedUsername)
                && activeAccounts.Contains(normalizedUsername))
            {
                normalized[normalizedUsername] = password ?? string.Empty;
            }
        }

        return normalized;
    }

    private sealed class DeviceUnlockAccountHistory
    {
        public List<string> CbssAccounts { get; set; } = [];
        public List<string> EmployAccounts { get; set; } = [];
        public Dictionary<string, string> CbssPasswords { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EmployPasswords { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
