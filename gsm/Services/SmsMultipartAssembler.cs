using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace gsm.Services;

public sealed record SmsConcatInfo(int Reference, int Total, int Sequence);
public sealed record DecodedSmsBody(string Content, SmsConcatInfo? Concatenation, bool WasHex = false, string? Sender = null);

public static class SmsBodyDecoder
{
    public static DecodedSmsBody Decode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(string.Empty, null);
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(StripInterleavedModemUrc)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !x.TrimStart().StartsWith("+CMGR:", StringComparison.OrdinalIgnoreCase) &&
                        !x.TrimStart().StartsWith("+QCMGR:", StringComparison.OrdinalIgnoreCase) &&
                        !x.TrimStart().StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase))
            .Where(x => !Regex.IsMatch(x.Trim(), @"^AT\+Q?CMGR\s*=", RegexOptions.IgnoreCase))
            .Where(x => !x.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase) && !x.Trim().Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (lines.Length == 0) return new(string.Empty, null);

        string hex = string.Concat(lines.Select(x => x.Trim()));
        bool looksHex = IsHex(hex);
        DecodedSmsBody decoded;
        if (looksHex && TryDecodeHex(hex, out var hexDecoded))
            decoded = hexDecoded;
        else if (looksHex && hex.Length > 16)
            // Never publish/delete a long undecodable PDU as if it were message text.
            // Returning empty makes the read queue retry and leaves the SMS on the SIM.
            decoded = new DecodedSmsBody(string.Empty, null, true);
        else
            decoded = new DecodedSmsBody(string.Join("\n", lines).Trim(), null);
        return decoded.Concatenation == null && TryParseQcmgrConcat(raw, out var qcmgrConcat)
            ? decoded with { Concatenation = qcmgrConcat }
            : decoded;
    }

    private static string StripInterleavedModemUrc(string line)
    {
        string trimmed = line.Trim();
        bool isModemUrc = Regex.IsMatch(
            trimmed,
            @"^\+(?:CTZE|CUSD|C(?:G|E)?REG|COPS|QIND|QSIMSTAT|CPIN|CCFC):",
            RegexOptions.IgnoreCase);
        if (!isModemUrc)
            return line;

        // EC20 can inject network-time, USSD and registration URCs in the middle
        // of CMGR/QCMGR/CMT. Depending on serial chunk boundaries the following
        // PDU is either on the next line or glued to that URC. Preserve only a
        // long trailing hex payload; a standalone URC is not SMS text.
        Match pdu = Regex.Match(
            trimmed,
            @"(?:^|\s)(?<pdu>[0-9A-F]{32,})\s*$",
            RegexOptions.IgnoreCase);
        return pdu.Success ? pdu.Groups["pdu"].Value : string.Empty;
    }

    private static bool TryDecodeHex(string hex, out DecodedSmsBody decoded)
    {
        decoded = new(hex, null, true);
        byte[] bytes;
        try { bytes = Convert.FromHexString(hex); } catch { return false; }

        // Some modem/firmware combinations ignore CMGF=1 and return a complete
        // SMS-DELIVER PDU. Decode that envelope before treating the bytes as UCS2.
        if (TryDecodeDeliverPdu(bytes, out decoded)) return true;

        int offset = 0;
        SmsConcatInfo? concat = null;
        if (TryFindUdh(bytes, out int headerBytes, out var parsed)) { offset = headerBytes; concat = parsed; }

        int count = bytes.Length - offset;
        if ((count & 1) != 0 && count > 0 && bytes[offset] == 0) { offset++; count--; } // EC20 alignment byte

        // Heuristic: If it's purely printable ASCII, it's not UCS2 (fixes VinaPhone sending GSM7 text as hex).
        bool isAscii = count > 0 && Enumerable.Range(offset, count).All(i => bytes[i] >= 0x20 && bytes[i] <= 0x7E || bytes[i] == 0x0A || bytes[i] == 0x0D);
        if (isAscii)
        {
            decoded = new(Encoding.ASCII.GetString(bytes, offset, count).TrimEnd('\0'), concat, true);
            return true;
        }

        bool ucs2 = count % 2 == 0 && Enumerable.Range(offset / 2, count / 2).Any(i => bytes[offset + i * 2] is 0x00 or 0x01 or 0x1E);
        if (!ucs2 && concat == null) return false;

        if ((count & 1) != 0) return false;
        decoded = new(Encoding.BigEndianUnicode.GetString(bytes, offset, count).TrimEnd('\0'), concat, true);
        return true;
    }

    private static bool TryDecodeDeliverPdu(byte[] pdu, out DecodedSmsBody decoded)
    {
        decoded = new(Convert.ToHexString(pdu), null, true);
        try
        {
            if (pdu.Length < 16) return false;
            int p = 1 + pdu[0]; // SMSC length excludes its own length byte
            if (p + 12 >= pdu.Length) return false;
            byte firstOctet = pdu[p++];
            if ((firstOctet & 0x03) != 0) return false; // SMS-DELIVER only

            int addressLength = pdu[p++];
            byte addressType = pdu[p++];
            bool alphaSender = (addressType & 0x70) == 0x50;
            int addressBytes = alphaSender ? (addressLength + 1) / 2 : (addressLength + 1) / 2;
            if (p + addressBytes + 10 > pdu.Length) return false;
            ReadOnlySpan<byte> address = pdu.AsSpan(p, addressBytes);
            p += addressBytes;
            string sender = alphaSender
                ? DecodeGsm7(address, Math.Max(1, addressLength * 4 / 7), 0)
                : DecodeSemiOctets(address, addressLength, (addressType & 0x70) == 0x10);

            p++; // PID
            byte dcs = pdu[p++];
            p += 7; // service-centre timestamp
            int userDataLength = pdu[p++];
            if (p > pdu.Length) return false;
            ReadOnlySpan<byte> userData = pdu.AsSpan(p);

            int headerBytes = 0;
            SmsConcatInfo? concat = null;
            if ((firstOctet & 0x40) != 0)
            {
                if (userData.Length == 0) return false;
                headerBytes = userData[0] + 1;
                if (headerBytes > userData.Length) return false;
                TryParseUdh(userData, out _, out concat);
            }

            string content;
            if ((dcs & 0x0C) == 0x08) // UCS2
            {
                int byteCount = Math.Min(userDataLength - headerBytes, userData.Length - headerBytes);
                if (byteCount < 0 || (byteCount & 1) != 0) return false;
                content = Encoding.BigEndianUnicode.GetString(userData.Slice(headerBytes, byteCount)).TrimEnd('\0');
            }
            else if ((dcs & 0x0C) == 0x00) // GSM 7-bit default alphabet
            {
                int headerSeptets = (headerBytes * 8 + 6) / 7;
                int textSeptets = Math.Max(0, userDataLength - headerSeptets);
                
                // Heuristic: If userDataLength equals the actual byte length and is > 8, 
                // it's mathematically impossible to be packed GSM-7 (where bytes = ceil(septets * 7/8)). 
                // It must be unpacked ASCII (network bug).
                bool isUnpackedAscii = userDataLength == userData.Length && userDataLength > 8;
                
                if (isUnpackedAscii)
                {
                    int byteCount = Math.Min(userDataLength - headerBytes, userData.Length - headerBytes);
                    content = Encoding.ASCII.GetString(userData.Slice(headerBytes, Math.Max(0, byteCount)));
                }
                else
                {
                    // UDH is padded to a septet boundary; text starts after the fill bits,
                    // not immediately at headerBytes * 8.
                    content = DecodeGsm7(userData, textSeptets, headerSeptets * 7);
                }
            }
            else
            {
                int byteCount = Math.Min(userDataLength - headerBytes, userData.Length - headerBytes);
                if (byteCount < 0) return false;
                content = Encoding.Latin1.GetString(userData.Slice(headerBytes, byteCount));
            }

            decoded = new(content.TrimEnd('\0'), concat, true, sender);
            return !string.IsNullOrWhiteSpace(decoded.Content);
        }
        catch { return false; }
    }

    private static string DecodeSemiOctets(ReadOnlySpan<byte> bytes, int digits, bool international)
    {
        var result = new StringBuilder(international ? "+" : "");
        foreach (byte value in bytes)
        {
            if (result.Length - (international ? 1 : 0) < digits) result.Append((char)('0' + (value & 0x0F)));
            if (result.Length - (international ? 1 : 0) < digits && (value >> 4) != 0x0F) result.Append((char)('0' + (value >> 4)));
        }
        return result.ToString();
    }

    private static string DecodeGsm7(ReadOnlySpan<byte> data, int septetCount, int startBit)
    {
        const string alphabet = "@\u00a3$\u00a5\u00e8\u00e9\u00f9\u00ec\u00f2\u00c7\n\u00d8\u00f8\r\u00c5\u00e5\u0394_\u03a6\u0393\u039b\u03a9\u03a0\u03a8\u03a3\u0398\u039e\u001b\u00c6\u00e6\u00df\u00c9 !\"#\u00a4%&'()*+,-./0123456789:;<=>?\u00a1ABCDEFGHIJKLMNOPQRSTUVWXYZ\u00c4\u00d6\u00d1\u00dc\u00a7\u00bfabcdefghijklmnopqrstuvwxyz\u00e4\u00f6\u00f1\u00fc\u00e0";
        var result = new StringBuilder(septetCount);
        for (int i = 0; i < septetCount; i++)
        {
            int bit = startBit + i * 7;
            int index = bit / 8;
            int shift = bit % 8;
            if (index >= data.Length) break;
            int value = (data[index] >> shift) & 0x7F;
            if (shift > 1 && index + 1 < data.Length) value |= (data[index + 1] << (8 - shift)) & 0x7F;
            result.Append(value < alphabet.Length ? alphabet[value] : '\uFFFD');
        }
        return result.ToString();
    }

    private static bool TryFindUdh(ReadOnlySpan<byte> data, out int payloadOffset, out SmsConcatInfo? concat)
    {
        // EC20 firmware variants sometimes place one UCS2 alignment byte before the UDH.
        // Only inspect offsets 0 and 1 to avoid mistaking normal message text for a header.
        for (int prefix = 0; prefix <= 1 && prefix < data.Length; prefix++)
        {
            if (prefix == 1 && data[0] != 0) break;
            if (TryParseUdh(data[prefix..], out int udhBytes, out concat))
            {
                payloadOffset = prefix + udhBytes;
                return true;
            }
        }
        payloadOffset = 0;
        concat = null;
        return false;
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
        // EC20/EC2x text mode returns four base fields for SMS-DELIVER and appends
        // uid,msg_seg,msg_total for multipart, for example:
        // +QCMGR: "REC UNREAD","sender",,"date",120,1,2
        // Some newer firmware exposes extra base fields, so parse the documented trailing triplet.
        if (fields.Length < 7 || !fields[0].StartsWith("REC ", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(fields[^3], out int reference) ||
            !int.TryParse(fields[^2], out int sequence) || !int.TryParse(fields[^1], out int total) ||
            total is < 2 or > 255 || sequence < 1 || sequence > total) return false;
        concat = new SmsConcatInfo(reference, total, sequence);
        return true;
    }
}

/// <summary>
/// Last-resort assembler for EC20 firmware that strips both UDH and QCMGR's
/// uid/msg_seg/msg_total. A concatenated UCS2 segment has exactly 67 characters
/// (GSM-7: 153); the final segment is shorter. Nothing is emitted or deleted
/// while the final segment is missing.
/// </summary>
public sealed class SmsImplicitMultipartAssembler
{
    private sealed class Buffer
    {
        public required int LastIndex { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        public List<string> Parts { get; } = new();
        public List<string> Indices { get; } = new();
    }

    private sealed class PortState
    {
        public object Gate { get; } = new();
        public Dictionary<string, Buffer> Buffers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> ProcessedIndices { get; } = new(StringComparer.Ordinal);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PortState> _ports =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _timeout;

    public SmsImplicitMultipartAssembler(TimeSpan? timeout = null) =>
        _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public SmsAssemblyResult Add(string port, string sender, string content, string index, DateTimeOffset? now = null)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        bool fullSegment = content.Length is 67 or 153;
        if (!int.TryParse(index, out int numericIndex) || string.IsNullOrWhiteSpace(port))
            return new(SmsAssemblyStatus.Invalid, null, Array.Empty<string>());

        PortState state = _ports.GetOrAdd(port, static _ => new PortState());
        string key = sender;
        lock (state.Gate)
        {
            RemoveExpiredCore(state, timestamp);
            string processedKey = $"{index}\u001f{content}";
            if (state.ProcessedIndices.ContainsKey(processedKey))
                return new(SmsAssemblyStatus.Duplicate, null, Array.Empty<string>());
            if (!state.Buffers.TryGetValue(key, out var buffer))
            {
                if (!fullSegment) return new(SmsAssemblyStatus.Invalid, null, Array.Empty<string>());
                buffer = new Buffer { LastIndex = numericIndex, LastUpdated = timestamp };
                buffer.Parts.Add(content);
                buffer.Indices.Add(index);
                state.Buffers[key] = buffer;
                return new(SmsAssemblyStatus.Waiting, null, Array.Empty<string>());
            }

            if (numericIndex == buffer.LastIndex && buffer.Indices.Contains(index))
                return new(SmsAssemblyStatus.Duplicate, null, Array.Empty<string>());
            if (numericIndex != buffer.LastIndex + 1)
                return new(SmsAssemblyStatus.Conflict, null, Array.Empty<string>());

            buffer.LastIndex = numericIndex;
            buffer.LastUpdated = timestamp;
            buffer.Parts.Add(content);
            buffer.Indices.Add(index);
            if (fullSegment) return new(SmsAssemblyStatus.Waiting, null, Array.Empty<string>());

            string complete = string.Concat(buffer.Parts);
            string[] indices = buffer.Indices.ToArray();
            for (int i = 0; i < buffer.Indices.Count; i++)
                state.ProcessedIndices[$"{buffer.Indices[i]}\u001f{buffer.Parts[i]}"] = timestamp;
            state.Buffers.Remove(key);
            return new(SmsAssemblyStatus.Completed, complete, indices);
        }
    }

    public int RemoveExpired(DateTimeOffset? now = null)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        int removed = 0;
        PortState[] states = _ports.Values.ToArray();
        Parallel.ForEach(states, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, states.Length)
        }, state =>
        {
            lock (state.Gate)
                Interlocked.Add(ref removed, RemoveExpiredCore(state, timestamp));
        });
        return removed;
    }

    public void ClearPort(string port) => _ports.TryRemove(port, out _);

    private int RemoveExpiredCore(PortState state, DateTimeOffset now)
    {
        string[] expired = state.Buffers.Where(x => now - x.Value.LastUpdated > _timeout).Select(x => x.Key).ToArray();
        foreach (string key in expired) state.Buffers.Remove(key);
        string[] processed = state.ProcessedIndices.Where(x => now - x.Value > _timeout).Select(x => x.Key).ToArray();
        foreach (string key in processed) state.ProcessedIndices.Remove(key);
        return expired.Length;
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
        public Dictionary<int, string> PartIndices { get; } = new();
    }
    private sealed class PortState
    {
        public object Gate { get; } = new();
        public Dictionary<string, Buffer> Buffers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> ProcessedIndices { get; } = new(StringComparer.Ordinal);
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PortState> _ports =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _timeout;
    public SmsMultipartAssembler(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public SmsAssemblyResult Add(string port, string sender, SmsConcatInfo concat, string content, string index, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(port) || concat.Total is < 2 or > 255 || concat.Sequence < 1 || concat.Sequence > concat.Total)
            return new(SmsAssemblyStatus.Invalid, null, Array.Empty<string>());
        PortState state = _ports.GetOrAdd(port, static _ => new PortState());
        lock (state.Gate)
        {
            RemoveExpiredCore(state, timestamp);
            string processedKey = $"{index}\u001f{sender}\u001f{concat.Reference}\u001f{concat.Total}\u001f{concat.Sequence}\u001f{content}";
            if (!string.IsNullOrWhiteSpace(index) && state.ProcessedIndices.ContainsKey(processedKey))
                return new(SmsAssemblyStatus.Duplicate, null, Array.Empty<string>());
            string key = $"{sender}\u001f{concat.Reference}\u001f{concat.Total}";
            if (!state.Buffers.TryGetValue(key, out var buffer))
                state.Buffers[key] = buffer = new Buffer { Total = concat.Total, LastUpdated = timestamp };
            if (buffer.Parts.TryGetValue(concat.Sequence, out string? old))
            {
                if (!string.Equals(old, content, StringComparison.Ordinal)) return new(SmsAssemblyStatus.Conflict, null, Array.Empty<string>());
                if (!string.IsNullOrWhiteSpace(index)) buffer.Indices.Add(index);
                return new(SmsAssemblyStatus.Duplicate, null, Array.Empty<string>());
            }
            buffer.Parts.Add(concat.Sequence, content);
            if (!string.IsNullOrWhiteSpace(index))
            {
                buffer.Indices.Add(index);
                buffer.PartIndices[concat.Sequence] = index;
            }
            buffer.LastUpdated = timestamp;
            if (Enumerable.Range(1, buffer.Total).Any(i => !buffer.Parts.ContainsKey(i)))
                return new(SmsAssemblyStatus.Waiting, null, Array.Empty<string>());
            string complete = string.Concat(Enumerable.Range(1, buffer.Total).Select(i => buffer.Parts[i]));
            string[] indices = buffer.Indices.ToArray();
            foreach (var completedPart in buffer.Parts)
            {
                // SIM storage indices are recycled immediately after CMGD. An index alone
                // is not a message identity; include the multipart identity and payload.
                string completedIndex = buffer.PartIndices.TryGetValue(completedPart.Key, out string? partIndex)
                    ? partIndex : index;
                string fingerprint = $"{completedIndex}\u001f{sender}\u001f{concat.Reference}\u001f{concat.Total}\u001f{completedPart.Key}\u001f{completedPart.Value}";
                state.ProcessedIndices[fingerprint] = timestamp;
            }
            state.Buffers.Remove(key);
            return new(SmsAssemblyStatus.Completed, complete, indices);
        }
    }

    public int RemoveExpired(DateTimeOffset? now = null)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        int removed = 0;
        PortState[] states = _ports.Values.ToArray();
        Parallel.ForEach(states, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, states.Length)
        }, state =>
        {
            lock (state.Gate)
                Interlocked.Add(ref removed, RemoveExpiredCore(state, timestamp));
        });
        return removed;
    }
    public void ClearPort(string port) => _ports.TryRemove(port, out _);
    private int RemoveExpiredCore(PortState state, DateTimeOffset now)
    {
        var keys = state.Buffers.Where(x => now - x.Value.LastUpdated > _timeout).Select(x => x.Key).ToArray();
        foreach (string key in keys) state.Buffers.Remove(key);
        var processed = state.ProcessedIndices.Where(x => now - x.Value > _timeout).Select(x => x.Key).ToArray();
        foreach (string key in processed) state.ProcessedIndices.Remove(key);
        return keys.Length;
    }
}
