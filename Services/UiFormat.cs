using System.Globalization;

namespace YuSwitch.Services;

/// <summary>
/// Consistent number formatting for the UI. Counts/latency use locale
/// thousands separators; token volumes use a compact k/M/亿 shorthand.
/// </summary>
public static class UiFormat
{
    /// <summary>Plain thousands-separated integer: 1234567 → "1,234,567".</summary>
    public static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Compact token volume: 512882 → "512.9k", 365879186 → "365.9M".</summary>
    public static string Tokens(long n) => n switch
    {
        >= 100_000_000 => $"{n / 100_000_000.0:0.##}亿",
        >= 1_000_000 => $"{n / 1_000_000.0:0.##}M",
        >= 10_000 => $"{n / 1000.0:0.#}k",
        _ => n.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>Latency in ms with thousands separator + unit.</summary>
    public static string Ms(long ms) => $"{ms.ToString("N0", CultureInfo.InvariantCulture)} ms";
}
