namespace Vestigo.Diagnostics;

/// <summary>Parses <c>pcm-rate-bits-channels</c> for a format check. PCM, 16-bit, one or two channels.</summary>
internal static class AudioProbeFormat
{
    public static bool TryParse(string? text, out int rate, out int bits, out int channels)
    {
        rate = 0;
        bits = 0;
        channels = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Trim().Split('-');
        if (parts.Length != 4 || !parts[0].Equals("pcm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out rate) || rate is < 8_000 or > 192_000)
        {
            return false;
        }

        if (!int.TryParse(parts[2], out bits) || bits != 16)
        {
            return false;
        }

        return int.TryParse(parts[3], out channels) && channels is 1 or 2;
    }

    public static string Describe(int rate, int bits, int channels) => $"pcm-{rate}-{bits}-{channels}";
}
