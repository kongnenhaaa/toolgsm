using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace gsm.Services;

public sealed record SmsConcatInfo(int Reference, int Total, int Sequence);
public sealed record DecodedSmsBody(
    string Content,
    SmsConcatInfo? Concatenation,
    bool WasHex = false,
    string? Sender = null,
    bool RecoveredMislabelledUcs2 = false);

/// <summary>
/// Sender aliases that have been observed to switch inside one carrier-created
/// multipart message. Keep this list deliberately narrow: treating every
/// numeric short code as equivalent could combine unrelated messages that
/// happen to reuse the same concatenation reference.
/// </summary>
internal static class SmsMultipartSenderAliases
{
    internal static readonly TimeSpan HandoffWindow = TimeSpan.FromMinutes(2);

    public static bool AreEquivalent(string left, string right)
    {
        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        return IsPair(left, right, "888", "565656");
    }

    private static bool IsPair(string left, string right, string first, string second) =>
        string.Equals(left.Trim(), first, StringComparison.OrdinalIgnoreCase)
        && string.Equals(right.Trim(), second, StringComparison.OrdinalIgnoreCase)
        || string.Equals(left.Trim(), second, StringComparison.OrdinalIgnoreCase)
        && string.Equals(right.Trim(), first, StringComparison.OrdinalIgnoreCase);
}

public static class SmsBodyDecoder
{
    private static readonly UnicodeEncoding StrictBigEndianUnicode = new(true, false, true);

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
            .ToArray();

        // Remove only the final modem terminator. Filtering every line equal to
        // "OK"/"ERROR" loses legitimate short SMS whose entire body is one of
        // those words (and was especially visible with short direct messages).
        // Keeping an earlier equal line preserves a body such as "OK" followed
        // by the transport's trailing OK.
        if (lines.Length > 1
            && Regex.IsMatch(raw, @"\+(?:Q?CMGR|CMT):", RegexOptions.IgnoreCase)
            && (lines[^1].Trim().Equals("OK", StringComparison.OrdinalIgnoreCase)
                || lines[^1].Trim().Equals("ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            lines = lines[..^1];
        }
        if (lines.Length == 0) return new(string.Empty, null);

        string hex = string.Concat(lines.Select(x => x.Trim()));
        bool looksHex = IsHex(hex);
        // A quoted CMGR/QCMGR/CMT header is explicit text mode. Carrier SMS
        // bodies can legitimately be hexadecimal-looking activation codes or
        // OTPs (for example "313233"). In that envelope, decode only a strong
        // UCS2/UDH structure; interpreting arbitrary printable byte pairs would
        // silently turn "313233" into "123" before the SIM slot is deleted.
        bool explicitTextEnvelope = Regex.IsMatch(
            raw,
            @"\+(?:Q?CMGR|CMT):\s*""",
            RegexOptions.IgnoreCase);
        DecodedSmsBody decoded;
        if (looksHex && TryDecodeHex(
                hex,
                explicitTextEnvelope,
                out var hexDecoded))
            decoded = hexDecoded;
        else if (looksHex && hex.Length > 16 && !explicitTextEnvelope)
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
            @"^\+(?:CMTI?|CSQ|COPS|C(?:G|E)?REG|CUSD|CLIP|QSIMSTAT|CPIN|QTONEDET|CTZE|QIND|CCFC|CMS\s+ERROR|CME\s+ERROR):",
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

    private static bool TryDecodeHex(
        string hex,
        bool explicitTextEnvelope,
        out DecodedSmsBody decoded)
    {
        decoded = new(hex, null, true);
        byte[] bytes;
        try { bytes = Convert.FromHexString(hex); } catch { return false; }

        // Some modem/firmware combinations ignore CMGF=1 and return a complete
        // SMS-DELIVER PDU. Decode that envelope before treating the bytes as UCS2.
        // A quoted header, however, explicitly identifies text mode; a literal
        // token that merely resembles a PDU must remain text.
        if (!explicitTextEnvelope && TryDecodeDeliverPdu(bytes, out decoded))
            return true;

        int offset = 0;
        SmsConcatInfo? concat = null;
        if (TryFindUdh(bytes, out int headerBytes, out var parsed)) { offset = headerBytes; concat = parsed; }

        int count = bytes.Length - offset;
        if ((count & 1) != 0 && count > 0 && bytes[offset] == 0) { offset++; count--; } // EC20 alignment byte

        // In text mode an explicit UDH is authoritative. Without one, require a
        // strong UTF-16BE byte pattern and printable decoded text. This retains
        // literal ASCII-hex tokens while still decoding UCS2 from CSCS="UCS2".
        if (explicitTextEnvelope && concat == null)
        {
            if (!TryDecodeStrongBareUcs2(
                    bytes.AsSpan(offset, count),
                    out string strongUcs2))
                return false;
            decoded = new(strongUcs2, null, true);
            return true;
        }

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

    private static bool TryDecodeStrongBareUcs2(
        ReadOnlySpan<byte> bytes,
        out string content)
    {
        content = string.Empty;
        if (bytes.Length < 4 || (bytes.Length & 1) != 0)
            return false;

        int codeUnits = bytes.Length / 2;
        int structuredHighBytes = 0;
        for (int i = 0; i < bytes.Length; i += 2)
        {
            // Basic Latin, Latin-1/Extended-A and Vietnamese precomposed code
            // points occupy these high-byte pages in UTF-16BE.
            if (bytes[i] is 0x00 or 0x01 or 0x1E)
                structuredHighBytes++;
        }

        // Short OTPs need two code units to be decodable, so require every pair
        // to match. Longer Vietnamese text tolerates a small number of symbols
        // outside the common pages while retaining a strong structural signal.
        int requiredStructuredBytes = codeUnits < 4
            ? codeUnits
            : (codeUnits * 70 + 99) / 100;
        if (structuredHighBytes < requiredStructuredBytes)
            return false;

        try { content = StrictBigEndianUnicode.GetString(bytes).TrimEnd('\0'); }
        catch (DecoderFallbackException) { return false; }
        if (string.IsNullOrWhiteSpace(content) || content.IndexOf('\0') >= 0)
            return false;

        foreach (char c in content)
        {
            if (char.IsControl(c) && c is not '\r' and not '\n' and not '\t')
            {
                content = string.Empty;
                return false;
            }
        }

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
            // TP-OA is mandatory in an SMS-DELIVER PDU. In particular, reject
            // zero-length addresses and values without the extension bit set.
            // A bare UTF-16BE body beginning "H VỤ..." otherwise looks like
            //   00 48 00 20 00 56 ...
            // (empty SMSC, plausible first octet, empty OA, invalid TOA, PID,
            // apparent 8-bit DCS) and its remaining Unicode bytes leak through
            // the Latin-1 branch as NUL/control characters.
            if (addressLength is <= 0 or > 20 || (addressType & 0x80) == 0)
                return false;

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
            int alphabet = dcs & 0x0C;
            int requiredUserDataBytes = alphabet == 0x00
                ? (userDataLength * 7 + 7) / 8
                : userDataLength;
            int maxUserDataLength = alphabet == 0x00 ? 160 : 140;
            if (userDataLength > maxUserDataLength || requiredUserDataBytes > userData.Length)
                return false;

            int headerBytes = 0;
            SmsConcatInfo? concat = null;
            if ((firstOctet & 0x40) != 0)
            {
                if (userData.Length == 0) return false;
                headerBytes = userData[0] + 1;
                if (headerBytes > userData.Length) return false;
                int headerUnits = alphabet == 0x00 ? (headerBytes * 8 + 6) / 7 : headerBytes;
                if (headerUnits > userDataLength) return false;
                TryParseUdh(userData, out _, out concat);
            }

            string content;
            if (alphabet == 0x08) // UCS2
            {
                int byteCount = Math.Min(userDataLength - headerBytes, userData.Length - headerBytes);
                if (byteCount < 0 || (byteCount & 1) != 0) return false;
                content = Encoding.BigEndianUnicode.GetString(userData.Slice(headerBytes, byteCount)).TrimEnd('\0');
            }
            else if (alphabet == 0x00) // GSM 7-bit default alphabet
            {
                int headerSeptets = (headerBytes * 8 + 6) / 7;
                int textSeptets = Math.Max(0, userDataLength - headerSeptets);

                // Some Vietnamese carrier/EC20 combinations store UTF-16BE user data while
                // incorrectly declaring DCS=0 (GSM 7-bit). Decoding those bytes as septets
                // produces the characteristic "@...@..." corruption and changes a 67-char
                // UCS2 segment into 153 fake GSM characters. Detect the strong UTF-16BE byte
                // structure before trusting the incorrect DCS so multipart sizing also stays
                // correct. A strict structural check keeps valid packed GSM-7 on this branch.
                if (TryDecodeMislabelledUcs2(userData, headerBytes, out string recoveredUcs2))
                {
                    decoded = new(recoveredUcs2, concat, true, sender, true);
                    return true;
                }

                // If userDataLength equals the actual byte length and is > 8, it is
                // mathematically impossible to be packed GSM-7. Some firmware returns
                // unpacked ASCII while retaining a GSM-7 DCS.
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

    private static bool TryDecodeMislabelledUcs2(
        ReadOnlySpan<byte> userData,
        int headerBytes,
        out string content)
    {
        content = string.Empty;
        if (headerBytes < 0 || headerBytes >= userData.Length) return false;

        // Firmware variants put the alignment zero either before the UTF-16BE text
        // (most often after a seven-byte UDH) or after the payload. Evaluate both
        // layouts, but only accept a candidate with a strong Unicode byte structure.
        Span<int> offsets = stackalloc int[3];
        Span<int> byteCounts = stackalloc int[3];
        int candidateCount = 0;
        int remaining = userData.Length - headerBytes;
        if ((remaining & 1) == 0)
        {
            offsets[candidateCount] = headerBytes;
            byteCounts[candidateCount++] = remaining;
        }
        else
        {
            if (remaining > 1 && userData[^1] == 0)
            {
                offsets[candidateCount] = headerBytes;
                byteCounts[candidateCount++] = remaining - 1;
            }
            if (remaining > 1 && userData[headerBytes] == 0)
            {
                offsets[candidateCount] = headerBytes + 1;
                byteCounts[candidateCount++] = remaining - 1;
            }
        }

        string? best = null;
        int bestScore = -1;
        for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            int offset = offsets[candidateIndex];
            int byteCount = byteCounts[candidateIndex];
            if (byteCount < 8 || (byteCount & 1) != 0) continue;

            ReadOnlySpan<byte> bytes = userData.Slice(offset, byteCount);
            int codeUnits = byteCount / 2;
            int structuredHighBytes = 0;
            for (int i = 0; i < byteCount; i += 2)
            {
                // Basic Latin, Latin-1/Extended-A and Vietnamese precomposed code
                // points occupy these high-byte pages in UTF-16BE.
                if (bytes[i] is 0x00 or 0x01 or 0x1E)
                    structuredHighBytes++;
            }

            // Requiring this pattern across most pairs avoids mistaking arbitrary
            // packed GSM data for Unicode merely because one byte happens to be zero.
            if (structuredHighBytes < 4 || structuredHighBytes * 100 < codeUnits * 70)
                continue;

            string candidate;
            try { candidate = StrictBigEndianUnicode.GetString(bytes).TrimEnd('\0'); }
            catch (DecoderFallbackException) { continue; }
            if (string.IsNullOrWhiteSpace(candidate) || candidate.IndexOf('\0') >= 0)
                continue;

            int acceptable = 0;
            int invalidControls = 0;
            foreach (char c in candidate)
            {
                if (char.IsControl(c) && c is not '\r' and not '\n' and not '\t')
                    invalidControls++;
                else
                    acceptable++;
            }
            if (invalidControls > 0 || acceptable * 100 < candidate.Length * 95)
                continue;

            if (structuredHighBytes > bestScore)
            {
                bestScore = structuredHighBytes;
                best = candidate;
            }
        }

        if (best == null) return false;
        content = best;
        return true;
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
        public required string Sender { get; init; }
        public required int Reference { get; init; }
        public required int Total { get; init; }
        public DateTimeOffset LastUpdated { get; set; }
        public Dictionary<int, string> Parts { get; } = new();
        public HashSet<string> Indices { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, string> PartIndices { get; } = new();
        public Dictionary<int, string> PartSenders { get; } = new();
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
            if (!string.IsNullOrWhiteSpace(index)
                && (state.ProcessedIndices.ContainsKey(processedKey)
                    || state.ProcessedIndices.Keys.Any(key => IsEquivalentFingerprint(
                        key, index, sender, concat, content))))
                return new(SmsAssemblyStatus.Duplicate, null, Array.Empty<string>());
            string key = $"{sender}\u001f{concat.Reference}\u001f{concat.Total}";
            if (!state.Buffers.TryGetValue(key, out var buffer))
            {
                // The observed VNPT handoff changes 888 to 565656 after part 1.
                // Adopt an alias buffer only when it is the sole recent,
                // non-conflicting candidate. A conflicting same-sequence part
                // starts its own buffer so two real messages cannot corrupt one
                // another merely because their references collided.
                KeyValuePair<string, Buffer>[] candidates = state.Buffers
                    .Where(pair => pair.Value.Reference == concat.Reference
                                   && pair.Value.Total == concat.Total
                                   && SmsMultipartSenderAliases.AreEquivalent(pair.Value.Sender, sender)
                                   && WithinAliasWindow(pair.Value.LastUpdated, timestamp)
                                   && IsPartCompatible(pair.Value, concat.Sequence, content))
                    .ToArray();
                if (candidates.Length == 1)
                {
                    key = candidates[0].Key;
                    buffer = candidates[0].Value;
                }
                else
                {
                    state.Buffers[key] = buffer = new Buffer
                    {
                        Sender = sender,
                        Reference = concat.Reference,
                        Total = concat.Total,
                        LastUpdated = timestamp
                    };
                }
            }
            if (buffer.Parts.TryGetValue(concat.Sequence, out string? old))
            {
                if (!string.Equals(old, content, StringComparison.Ordinal)) return new(SmsAssemblyStatus.Conflict, null, Array.Empty<string>());
                if (!string.IsNullOrWhiteSpace(index)) buffer.Indices.Add(index);
                return new(SmsAssemblyStatus.Duplicate, null, Array.Empty<string>());
            }
            buffer.Parts.Add(concat.Sequence, content);
            buffer.PartSenders[concat.Sequence] = sender;
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
                string completedSender = buffer.PartSenders.TryGetValue(completedPart.Key, out string? partSender)
                    ? partSender : sender;
                string fingerprint = $"{completedIndex}\u001f{completedSender}\u001f{concat.Reference}\u001f{concat.Total}\u001f{completedPart.Key}\u001f{completedPart.Value}";
                state.ProcessedIndices[fingerprint] = timestamp;
            }
            state.Buffers.Remove(key);
            return new(SmsAssemblyStatus.Completed, complete, indices);
        }
    }

    public void ForgetMessage(string port, string sender, SmsConcatInfo concat)
    {
        if (!_ports.TryGetValue(port, out PortState? state)) return;
        lock (state.Gate)
        {
            foreach (string key in state.Buffers
                         .Where(pair => pair.Value.Reference == concat.Reference
                                        && pair.Value.Total == concat.Total
                                        && SmsMultipartSenderAliases.AreEquivalent(pair.Value.Sender, sender))
                         .Select(pair => pair.Key)
                         .ToArray())
                state.Buffers.Remove(key);
            foreach (string key in state.ProcessedIndices.Keys
                         .Where(key => IsEquivalentFingerprintIdentity(key, sender, concat))
                         .ToArray())
                state.ProcessedIndices.Remove(key);
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

    private static bool IsPartCompatible(Buffer buffer, int sequence, string content) =>
        !buffer.Parts.TryGetValue(sequence, out string? existing)
        || string.Equals(existing, content, StringComparison.Ordinal);

    private static bool WithinAliasWindow(DateTimeOffset left, DateTimeOffset right) =>
        (left - right).Duration() <= SmsMultipartSenderAliases.HandoffWindow;

    private static bool IsEquivalentFingerprint(
        string fingerprint,
        string index,
        string sender,
        SmsConcatInfo concat,
        string content)
    {
        string[] fields = fingerprint.Split('\u001f');
        return fields.Length == 6
               && string.Equals(fields[0], index, StringComparison.Ordinal)
               && SmsMultipartSenderAliases.AreEquivalent(fields[1], sender)
               && int.TryParse(fields[2], out int reference) && reference == concat.Reference
               && int.TryParse(fields[3], out int total) && total == concat.Total
               && int.TryParse(fields[4], out int sequence) && sequence == concat.Sequence
               && string.Equals(fields[5], content, StringComparison.Ordinal);
    }

    private static bool IsEquivalentFingerprintIdentity(
        string fingerprint,
        string sender,
        SmsConcatInfo concat)
    {
        string[] fields = fingerprint.Split('\u001f');
        return fields.Length == 6
               && SmsMultipartSenderAliases.AreEquivalent(fields[1], sender)
               && int.TryParse(fields[2], out int reference) && reference == concat.Reference
               && int.TryParse(fields[3], out int total) && total == concat.Total;
    }
}
