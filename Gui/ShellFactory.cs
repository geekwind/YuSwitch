namespace YuSwitch.Gui;

/// <summary>
/// Picks the desktop shell for the current platform. Windows runs the
/// WinForms/WebView2 shell; macOS runs the Photino (WKWebView) shell. Returns
/// null when no GUI shell is available (Linux for now), so the caller falls
/// through to headless mode.
/// </summary>
public static class ShellFactory
{
    public static IShell? Create()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
            return new WinFormsShell();
#endif
        if (OperatingSystem.IsMacOS())
            return new PhotinoShell();
        return null;
    }
}
