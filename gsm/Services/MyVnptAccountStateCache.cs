using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace gsm.Services;

/// <summary>
/// Remembers confirmed MyVNPT accounts without storing phone numbers. This
/// avoids a stale authen_check_account response forcing a register OTP before
/// the real forgot-password OTP on a later run.
/// </summary>
internal sealed class MyVnptAccountStateCache
{
    private static readonly Lazy<MyVnptAccountStateCache> Shared = new(() =>
        new MyVnptAccountStateCache(
            AppPaths.ForUserDataFile("myvnpt_account_state.json")));

    private readonly object _sync = new();
    private readonly string _path;
    private readonly HashSet<string> _knownAccountHashes =
        new(StringComparer.Ordinal);
    private bool _loaded;

    public static MyVnptAccountStateCache Default => Shared.Value;

    internal MyVnptAccountStateCache(string path)
    {
        _path = path;
    }

    public bool IsKnownAccount(string normalizedPhone)
    {
        string hash = HashPhone(normalizedPhone);
        if (hash.Length == 0) return false;

        lock (_sync)
        {
            EnsureLoaded();
            return _knownAccountHashes.Contains(hash);
        }
    }

    public void MarkAccountExists(string normalizedPhone) =>
        Change(normalizedPhone, exists: true);

    public void MarkAccountMissing(string normalizedPhone) =>
        Change(normalizedPhone, exists: false);

    private void Change(string normalizedPhone, bool exists)
    {
        string hash = HashPhone(normalizedPhone);
        if (hash.Length == 0) return;

        lock (_sync)
        {
            EnsureLoaded();
            bool changed = exists
                ? _knownAccountHashes.Add(hash)
                : _knownAccountHashes.Remove(hash);
            if (changed) Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            string[]? entries = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(_path));
            if (entries == null) return;
            foreach (string entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    _knownAccountHashes.Add(entry.Trim());
            }
        }
        catch
        {
            // A cache miss only costs one fallback OTP flow.
        }
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            string tempPath = _path + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(
                    _knownAccountHashes.OrderBy(value => value).ToArray()),
                new UTF8Encoding(false));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            // Never fail a password workflow because the optimization cache
            // cannot be persisted.
        }
    }

    private static string HashPhone(string? normalizedPhone)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhone)) return string.Empty;
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedPhone.Trim()));
        return Convert.ToHexString(digest);
    }
}
