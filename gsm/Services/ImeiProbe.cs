using System.Text.RegularExpressions;

namespace gsm.Services;

internal static class ImeiProbe
{
    internal static readonly string[] CommandOrder = ["AT+CGSN", "AT+GSN"];

    internal static string ExtractImei(string? value) =>
        Regex.Match(
            value ?? string.Empty,
            @"(?<!\d)\d{15}(?!\d)",
            RegexOptions.CultureInvariant).Value;

    internal static string ParseSuccessfulResponse(string? response)
    {
        string value = response ?? string.Empty;
        bool hasOk = Regex.IsMatch(
            value,
            @"(?:^|\r?\n)\s*OK\s*(?:\r?\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool hasError = Regex.IsMatch(
            value,
            @"\+(?:CME|CMS)\s+ERROR:|\bERROR\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return hasOk && !hasError ? ExtractImei(value) : string.Empty;
    }

    internal static async Task<string> ReadAsync(
        Func<string, CancellationToken, Task<string>> sendCommand,
        int attempts = 3,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sendCommand);

        int attemptCount = Math.Max(1, attempts);
        TimeSpan delay = retryDelay ?? TimeSpan.FromMilliseconds(350);
        for (int attempt = 1; attempt <= attemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string command in CommandOrder)
            {
                try
                {
                    string response = await sendCommand(command, cancellationToken)
                        .ConfigureAwait(false);
                    string imei = ParseSuccessfulResponse(response);
                    if (!string.IsNullOrWhiteSpace(imei)) return imei;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // IMEI is optional for keeping a live SIM online. Try the
                    // fallback command/next attempt without aborting activation.
                }
            }

            if (attempt < attemptCount && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return string.Empty;
    }
}
