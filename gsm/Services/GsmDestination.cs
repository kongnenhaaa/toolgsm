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
        return destination.Length is > 0 and <= MaxDestinationLength
            && !destination.Any(char.IsControl)
            && !destination.Contains(';');
    }
}
