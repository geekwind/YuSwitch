using Microsoft.AspNetCore.Builder;

namespace YuSwitch.Gui;

/// <summary>
/// Desktop shell contract. Implementations own single-instance detection, port
/// pre-flight, bringing up the server, showing the window/tray icon, and
/// stopping the server when the shell exits. One shell per platform: WinForms/
/// WebView2 on Windows, Photino (WKWebView) on macOS.
/// </summary>
public interface IShell : IDisposable
{
    /// <summary>
    /// Blocks until the shell exits; the server is started (if it was free)
    /// and stopped inside. Returns when the app should terminate.
    /// </summary>
    Task RunAsync(WebApplication app, int port, CancellationToken ct);
}
