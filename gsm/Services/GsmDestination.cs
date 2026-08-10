namespace gsm.Services;

/// <summary>
/// Keeps destination input flexible for carrier short codes while preventing a value
/// from escaping the AT command in which it is embedded.
/// </summary>
public static class GsmDestination
{
    private const int MaxDestinationLength = 64;

    public static bool TryNormalizeSms(string? input, out string destination)
    {
        destination = input?.Trim() ?? string.Empty;
        return destination.Length is > 0 and <= MaxDestinationLength
            && !destination.Any(char.IsControl)
            && !destination.Contains('"')
            && !destination.Contains('\x1A');
    }

    public static bool TryNormalizeDial(string? input, out string destination)
    {
        destination = input?.Trim() ?? string.Empty;
        bool isValid = destination.Length is > 0 and <= MaxDestinationLength
            && !destination.Any(char.IsControl)
            && !destination.Contains(';');
        if (!isValid)
            return false;

        // Some Quectel/VinaPhone combinations reject a Vietnamese fixed-line
        // number in domestic form (for example ATD02873079214;) even though a
        // handset silently converts it. Send fixed-line numbers as E.164 while
        // preserving mobile numbers, carrier short codes and USSD sequences.
        if (destination.Length == 11
            && destination.StartsWith("02", StringComparison.Ordinal)
            && destination.All(char.IsDigit))
        {
            destination = "+84" + destination[1..];
        }
        else if (destination.Length == 12
            && destination.StartsWith("842", StringComparison.Ordinal)
            && destination.All(char.IsDigit))
        {
            destination = "+" + destination;
        }

        return true;
    }
}
