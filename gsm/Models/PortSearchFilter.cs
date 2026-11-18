using System.Text;
using System.Text.RegularExpressions;

namespace gsm.Models;

public static class PortSearchFilter
{
    private static readonly Regex TermSeparator = new(
        @"[\s,;|]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IEnumerable<SimPort> Filter(
        IEnumerable<SimPort> ports,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(ports);

        IReadOnlyList<string> terms = ParseTerms(query);
        if (terms.Count == 0) return ports;

        // Where keeps the source GSM order. The order of pasted search terms
        // must never reorder COM rows.
        return ports.Where(port => terms.Any(term => Matches(port, term)));
    }

    public static IReadOnlyList<string> ParseTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();

        return TermSeparator.Split(query)
            .Select(term => term.Trim().Trim('"', '\'', '[', ']'))
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Matches(SimPort port, string term) =>
        Contains(port.PhoneNumber, term)
        || PhoneDigitsMatch(port.PhoneNumber, term)
        || Contains(port.PortName, term)
        || Contains(port.Serial, term)
        || Contains(port.Imei, term);

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static bool PhoneDigitsMatch(string? phoneNumber, string term)
    {
        string phoneDigits = DigitsOnly(phoneNumber);
        string termDigits = DigitsOnly(term);
        if (phoneDigits.Length < 9 || termDigits.Length < 9) return false;

        return NormalizeVietnamPhone(phoneDigits).Equals(
            NormalizeVietnamPhone(termDigits),
            StringComparison.Ordinal);
    }

    private static string NormalizeVietnamPhone(string digits)
    {
        if (digits.Length == 11 && digits.StartsWith("84", StringComparison.Ordinal))
            return "0" + digits[2..];
        if (digits.Length == 9) return "0" + digits;
        return digits;
    }

    private static string DigitsOnly(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsAsciiDigit(character)) result.Append(character);
        }
        return result.ToString();
    }
}
