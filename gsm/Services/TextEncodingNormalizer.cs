using System;
using System.Linq;
using System.Text;

namespace gsm.Services;

public static class TextEncodingNormalizer
{
    private static readonly Encoding Windows1252;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static TextEncodingNormalizer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string RepairMojibake(string? value)
    {
        string current = value ?? string.Empty;
        for (int pass = 0; pass < 3; pass++)
        {
            int oldScore = MojibakeScore(current);
            if (oldScore == 0) break;
            try
            {
                string candidate = StrictUtf8.GetString(Windows1252.GetBytes(current));
                if (MojibakeScore(candidate) >= oldScore) break;
                current = candidate;
            }
            catch (EncoderFallbackException) { break; }
            catch (DecoderFallbackException) { break; }
        }
        return current;
    }

    private static int MojibakeScore(string text)
    {
        string[] markers = ["Ã", "Â", "Ä", "Æ", "áº", "á»", "â€", "â†", "âš", "ðŸ"];
        int score = markers.Sum(marker => Count(text, marker) * 3);
        score += text.Count(c => c == '\uFFFD') * 10;
        return score;
    }

    private static int Count(string text, string marker)
    {
        int count = 0;
        for (int at = 0; (at = text.IndexOf(marker, at, StringComparison.Ordinal)) >= 0; at += marker.Length)
            count++;
        return count;
    }
}
