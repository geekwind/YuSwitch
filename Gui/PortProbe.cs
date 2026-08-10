namespace YuSwitch.Gui;

public enum PortProbe { Free, Ours, Foreign }

/// <summary>
/// Probes /health on a target base URL to classify the port owner. Used by the
/// desktop shells so an already-running instance is opened instead of started,
/// and a foreign HTTP app on the port yields a clear prompt instead of the
/// WebView silently loading the wrong page.
/// </summary>
public static class PortProbeService
{
    public static async Task<PortProbe> ProbeAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync(baseUrl + "/health");
            var body = await resp.Content.ReadAsStringAsync();
            return body.Contains("\"app\":\"YuSwitch\"", StringComparison.OrdinalIgnoreCase)
                ? PortProbe.Ours : PortProbe.Foreign;
        }
        catch
        {
            return PortProbe.Free;
        }
    }
}
