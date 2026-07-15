using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace gsm.Services;

public sealed record SmsConcatInfo(int Reference, int Total, int Sequence);
public sealed record DecodedSmsBody(string Content, SmsConcatInfo? Concatenation);

public static class SmsBodyDecoder
{
    public static DecodedSmsBody Decode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(string.Empty, null);
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !x.TrimStart().StartsWith("+CMGR:", StringComparison.OrdinalIgnoreCase) &&
                        !x.TrimStart().StartsWith("+QCMGR:", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase) && !x.Trim().Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (lines.Length == 0) return new(string.Empty, null);

        string hex = string.Concat(lines.Select(x => x.Trim()));
        DecodedSmsBody decoded = IsHex(hex) && TryDecodeHex(hex, out var hexDecoded)
            ? hexDecoded
            : new DecodedSmsBody(string.Join("\n", lines).Trim(), null);
        return decoded.Concatenation == null && TryParseQcmgrConcat(raw, out var qcmgrConcat)
            ? decoded with { Concatenation = qcmgrConcat }
            : decoded;
    }

    private static bool TryDecodeHex(string hex, out DecodedSmsBody decoded)
    {
        decoded = new(hex, null);
        byte[] bytes;
        try { bytes = Convert.FromHexString(hex); } catch { return false; }

        int offset = 0;
        SmsConcatInfo? concat = null;
        if (TryParseUdh(bytes, out int headerBytes, out var parsed)) { offset = headerBytes; concat = parsed; }
        else
        {
            bool ucs2 = bytes.Length % 2 == 0 && Enumerable.Range(0, bytes.Length / 2).Any(i => bytes[i * 2] is 0x00 or 0x01 or 0x1E);
            if (!ucs2) return false; // Do not turn a plain numeric OTP like 1234 into an arbitrary glyph.
        }

        int count = bytes.Length - offset;
        if ((count & 1) != 0 && count > 0 && bytes[offset] == 0) { offset++; count--; } // EC20 alignment byte
        if ((count & 1) != 0) return false;
        decoded = new(Encoding.BigEndianUnicode.GetString(bytes, offset, count).TrimEnd('\0'), concat);
        return true;
    }

    public static bool TryParseUdh(ReadOnlySpan<byte> data, out int headerBytes, out SmsConcatInfo? concat)
    {
        headerBytes = 0; concat = null;
        if (data.Length < 2) return false;
        headerBytes = data[0] + 1;
        if (data[0] < 2 || headerBytes > data.Length) { headerBytes = 0; return false; }
        int p = 1;
        while (p + 2 <= headerBytes)
        {
            byte iei = data[p++]; int length = data[p++];
            if (p + length > headerBytes) { headerBytes = 0; concat = null; return false; }
            if (iei == 0 && length == 3) concat = new(data[p], data[p + 1], data[p + 2]);
            else if (iei == 8 && length == 4) concat = new((data[p] << 8) | data[p + 1], data[p + 2], data[p + 3]);
            p += length;
        }
        return concat != null;
    }

    private static bool IsHex(string value) => value.Length >= 4 && (value.Length & 1) == 0 && value.All(Uri.IsHexDigit);

    private static bool TryParseQcmgrConcat(string raw, out SmsConcatInfo? concat)
    {
        concat = null;
        Match header = Regex.Match(raw, @"\+QCMGR:([^\r\n]*)", RegexOptions.IgnoreCase);
        if (!header.Success) return false;
        string[] fields = Regex.Matches(header.Groups[1].Value, "(?:^|,)\\s*(?:\"([^\"]*)\"|([^,]*))")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Trim())
            .ToArray();
        // SMS-DELIVER has 11 base fields; Quectel appends uid, segment and total only for multipart.
        if (fields.Length < 14 || !int.TryParse(fields[^3], out int reference) ||
            !int.TryParse(fields[^2], out int sequence) || !int.TryParse(fields[^1], out int total) ||
            total is < 2 or > 255 || sequence < 1 || sequence > total) return false;
        concat = new SmsConcatInfo(reference, total, sequence);
        return true;
    }
}

public enum SmsAssemblyStatus { Waiting, Completed, Duplicate, Invalid, Conflict }
public sealed record SmsAssemblyResult(SmsAssemblyStatus Status, string? Content, IReadOnlyList<string> MessageIndices);

public sealed class SmsMultipartAssembler
{
    private sealed class Buffer
    {
        public required int Total { get; init; }
        public DateTimeOffset LastUpdated { get; set; }
        public Dictionary<int, string> Parts { get; } = new();
        public HashSet<string> Indices { get; } = new(StringComparer.Ordinal);
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Buffer> _buffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _processedIndices = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;
    public SmsMultipartAssembler(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public SmsAssemblyResult Add(string port, string sender, SmsConcatInfo concat, string content, string index, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            RemoveExpiredCore(timestamp);
            string processedKey = $"{port}\u001f{index}";
            if (!string.IsNullOrWhiteSpace(index) && _processedIndices.ContainsKey(processedKey))
                return new(SmsAssemblyStatus.Duplicate, null, Array.Empty<string>());
            if (string.IsNullOrWhiteSpace(port) || concat.Total is < 2 or > 255 || concat.Sequence < 1 || concat.Sequence > concat.Total)
                return new(SmsAssemblyStatus.Invalid, null, Array.Empty<string>());
            string key = $"{port}\u001f{sender}\u001f{concat.Reference}\u001f{concat.Total}";
            if (!_buffers.TryGetValue(key, out var buffer))
                _buffers[key] = buffer = new Buffer { Total = concat.Total, LastUpdated = timestamp };
            if (buffer.Parts.TryGetValue(concat.Sequence, out string? old))
            {
                if (!string.Equals(old, content, StringComparison.Ordinal)) return new(SmsAssemblyStatus.Conflict, null, Array.Empty<string>());
                if (!string.IsNullOrWhiteSpace(index)) buffer.Indices.Add(index);
                buffer.LastUpdated = timestamp;
                return new(SmsAssemblyStatus.Duplicate, null, Array.Empty<string>());
            }
            buffer.Parts.Add(concat.Sequence, content);
            if (!string.IsNullOrWhiteSpace(index)) buffer.Indices.Add(index);
            buffer.LastUpdated = timestamp;
            if (Enumerable.Range(1, buffer.Total).Any(i => !buffer.Parts.ContainsKey(i)))
                return new(SmsAssemblyStatus.Waiting, null, Array.Empty<string>());
            string complete = string.Concat(Enumerable.Range(1, buffer.Total).Select(i => buffer.Parts[i]));
            string[] indices = buffer.Indices.ToArray();
            foreach (string completedIndex in indices) _processedIndices[$"{port}\u001f{completedIndex}"] = timestamp;
            _buffers.Remove(key);
            return new(SmsAssemblyStatus.Completed, complete, indices);
        }
    }

    public int RemoveExpired(DateTimeOffset? now = null) { lock (_gate) return RemoveExpiredCore(now ?? DateTimeOffset.UtcNow); }
    private int RemoveExpiredCore(DateTimeOffset now)
    {
        var keys = _buffers.Where(x => now - x.Value.LastUpdated > _timeout).Select(x => x.Key).ToArray();
        foreach (string key in keys) _buffers.Remove(key);
        var processed = _processedIndices.Where(x => now - x.Value > _timeout).Select(x => x.Key).ToArray();
        foreach (string key in processed) _processedIndices.Remove(key);
        return keys.Length;
    }
}
