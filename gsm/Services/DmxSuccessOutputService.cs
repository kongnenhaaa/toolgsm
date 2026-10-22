using System.IO;
using System.Linq;
using System.Text;

namespace gsm.Services;

/// <summary>
/// Keeps two plain-text lists of successful Dien May Xanh phone numbers:
/// the latest batch and the de-duplicated total across batches.
/// </summary>
public sealed class DmxSuccessOutputService
{
    public const string OutputDirectoryName = "DienMayXanh";
    public const string LatestSessionFileName =
        "dmx_sdt_thanh_cong_phien_gan_nhat.txt";
    public const string AggregateFileName =
        "dmx_sdt_thanh_cong_tong.txt";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly object FileSync = new();
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private readonly string _outputDirectory;

    public DmxSuccessOutputService()
        : this(Path.Combine(AppPaths.UserDataDirectory, OutputDirectoryName))
    {
    }

    internal DmxSuccessOutputService(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _outputDirectory = Path.GetFullPath(outputDirectory);
    }

    public string OutputDirectory => _outputDirectory;
    public string LatestSessionPath =>
        Path.Combine(_outputDirectory, LatestSessionFileName);
    public string AggregatePath =>
        Path.Combine(_outputDirectory, AggregateFileName);

    /// <summary>
    /// Starts a new batch. The latest-session list is reset while the aggregate
    /// list is preserved. Both files are created so an empty-success run is
    /// represented by an empty latest-session file.
    /// </summary>
    public void BeginSession(DateTime? utcNow = null)
    {
        lock (FileSync)
        {
            DeleteExpiredOutputsCore(
                _outputDirectory,
                utcNow ?? DateTime.UtcNow);
            Directory.CreateDirectory(_outputDirectory);
            WriteLinesAtomically(LatestSessionPath, []);
            if (!File.Exists(AggregatePath))
                WriteLinesAtomically(AggregatePath, []);
        }
    }

    /// <summary>
    /// Adds one successful phone number to both lists. Each list contains one
    /// normalized domestic phone number per line and never contains duplicates.
    /// </summary>
    public void RecordSuccess(string phone)
    {
        string normalizedPhone = DmxAccountService.NormalizePhone(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            throw new ArgumentException(
                "Số điện thoại Điện Máy Xanh không hợp lệ.",
                nameof(phone));
        }

        lock (FileSync)
        {
            Directory.CreateDirectory(_outputDirectory);
            AppendUniquePhone(LatestSessionPath, normalizedPhone);
            AppendUniquePhone(AggregatePath, normalizedPhone);
        }
    }

    /// <summary>
    /// Deletes only the two known output files after seven days without a
    /// write. This is called at startup, hourly, and before a new batch.
    /// </summary>
    public static void DeleteExpiredOutputs(DateTime? utcNow = null)
    {
        string directory = Path.Combine(
            AppPaths.UserDataDirectory,
            OutputDirectoryName);
        DeleteExpiredOutputs(directory, utcNow);
    }

    internal static void DeleteExpiredOutputs(
        string outputDirectory,
        DateTime? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        lock (FileSync)
        {
            DeleteExpiredOutputsCore(
                Path.GetFullPath(outputDirectory),
                utcNow ?? DateTime.UtcNow);
        }
    }

    private static void DeleteExpiredOutputsCore(
        string outputDirectory,
        DateTime utcNow)
    {
        if (!Directory.Exists(outputDirectory)) return;

        DateTime cutoffUtc = utcNow - Retention;
        foreach (string fileName in new[]
                 {
                     LatestSessionFileName,
                     AggregateFileName
                 })
        {
            string path = Path.Combine(outputDirectory, fileName);
            try
            {
                if (File.Exists(path)
                    && File.GetLastWriteTimeUtc(path) <= cutoffUtc)
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Locked files are retried by the next hourly cleanup.
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                Directory.Delete(outputDirectory);
        }
        catch
        {
        }
    }

    private static void AppendUniquePhone(string path, string phone)
    {
        List<string> phones = ReadPhones(path);
        if (phones.Contains(phone, StringComparer.Ordinal)) return;
        phones.Add(phone);
        WriteLinesAtomically(path, phones);
    }

    private static List<string> ReadPhones(string path)
    {
        if (!File.Exists(path)) return [];
        return File.ReadLines(path, Utf8WithoutBom)
            .Select(DmxAccountService.NormalizePhone)
            .Where(static phone => !string.IsNullOrWhiteSpace(phone))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void WriteLinesAtomically(
        string path,
        IEnumerable<string> lines)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(temporaryPath, lines, Utf8WithoutBom);
            File.Move(temporaryPath, path, overwrite: true);
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
}
