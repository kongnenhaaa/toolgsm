using gsm.Services;

namespace gsm.Tests;

internal static class TestImeiFactory
{
    private static int _serial;

    internal static string Create()
    {
        int serial = Interlocked.Increment(ref _serial) % 1_000_000;
        string body = $"35148816{serial:D6}";
        for (int checkDigit = 0; checkDigit <= 9; checkDigit++)
        {
            string candidate = body + checkDigit;
            if (ImeiManagementService.IsValidImei(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Unable to create test IMEI.");
    }
}
