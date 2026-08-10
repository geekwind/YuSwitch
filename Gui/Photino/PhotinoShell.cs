using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Photino.NET;
using Serilog;

namespace YuSwitch.Gui;

/// <summary>
/// macOS desktop shell: a Photino window hosting the system WKWebView pointed
/// at the local gateway. Mirrors the Windows shell's single-instance guard,
/// port pre-flight, and close-to-tray: closing the window hides it and leaves
/// the gateway resident behind a menu-bar item (打开界面 / 在浏览器中打开 / 退出).
/// The menu-bar icon and window hide/show are owned by a small native helper
/// (libYuSwitchHelper.dylib, compiled into the .app by make_macos_app.sh) because
/// Photino.NET has no status-item API. Dev runs without the helper degrade to
/// close = quit.
/// </summary>
public sealed class PhotinoShell : IShell
{
    // --- Native menu-bar helper ---
    private static bool _helperAvailable = true;

    static PhotinoShell()
    {
        // The helper dylib lives next to the executable (Contents/MacOS/ inside
        // the .app). The CWD is pinned to ~/Library/Application Support/YuSwitch,
        // so resolve the library explicitly relative to the exe.
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(PhotinoShell).Assembly,
                (name, assembly, path) =>
                {
                    var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
                    var lib = name.StartsWith("lib", StringComparison.Ordinal) ? name : "lib" + name;
                    if (!OperatingSystem.IsWindows() && !lib.EndsWith(".dylib", StringComparison.Ordinal))
                        lib += ".dylib";
                    return NativeLibrary.Load(Path.Combine(dir, lib), assembly, path);
                });
        }
        catch { _helperAvailable = false; }
    }

    [DllImport("YuSwitchHelper")]
    private static extern void YSInstallStatusItem(IntPtr window, IntPtr onOpenBrowser, IntPtr onQuit);

    [DllImport("YuSwitchHelper")]
    private static extern void YSHideWindow(IntPtr window);

    // Native callbacks. The delegate instances are rooted here so the pointers
    // handed to the dylib stay valid for the process lifetime.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void YSVoidCallback();

    private static readonly YSVoidCallback _onOpenBrowser = OnOpenBrowser;
    private static readonly YSVoidCallback _onQuit = OnQuit;
    private static readonly IntPtr _onOpenBrowserPtr = Marshal.GetFunctionPointerForDelegate(_onOpenBrowser);
    private static readonly IntPtr _onQuitPtr = Marshal.GetFunctionPointerForDelegate(_onQuit);

    // Bridged into the native callbacks (they can't capture instance state).
    // One window per process, so statics are fine.
    private static PhotinoWindow? _window;
    private static string _localUrl = "";
    private static volatile bool _quitting;

    private static void OnOpenBrowser() => OpenInSystemBrowser(_localUrl);

    private static void OnQuit()
    {
        _quitting = true;
        Task.Run(() => _window?.Close());  // off the AppKit main thread
    }

    public async Task RunAsync(WebApplication app, int port, CancellationToken ct)
    {
        var localUrl = $"http://localhost:{port}";

        // Single instance: another YuSwitch already answers on this port → open
        // the system browser and exit. A foreign HTTP app on the port → warn.
        var probe = await PortProbeService.ProbeAsync(localUrl);
        if (probe == PortProbe.Ours)
        {
            OpenInSystemBrowser(localUrl);
            return;
        }
        if (probe == PortProbe.Foreign)
        {
            MacAlert("禹枢 启动失败",
                $"端口 {port} 已被其他程序占用。\n\n请关闭占用该端口的程序，或在「设置 → 监听 / 网络」中修改监听端口后重试。");
            return;
        }

        // Start the server BEFORE showing any window so a bind failure is a
        // visible alert instead of a forever-blank WebView.
        try
        {
            await app.StartAsync(ct);
            Log.Information("Gateway started at {Url} (desktop mode)", localUrl);
        }
        catch (Exception ex)
        {
            var baseEx = ex.GetBaseException();
            Log.Fatal(ex, "Gateway failed to start");
            MacAlert("禹枢 启动失败",
                $"启动失败：{baseEx.Message}\n\n详细日志见 ~/Library/Application Support/YuSwitch/logs");
            return;
        }

        await RunWindowAsync(localUrl);

        // Window closed via menu-bar 退出 → stop the server cleanly.
        await app.StopAsync();
    }

    /// <summary>Runs the Photino window on its own foreground thread. Photino
    /// drives the native run loop from the thread that creates the window, so
    /// this thread owns both creation and WaitForClose. (On macOS the underlying
    /// WebKit app marshals AppKit calls onto the main queue; if a future .NET
    /// version stops doing that, hoist the creation before the first await.)</summary>
    private static Task RunWindowAsync(string localUrl)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ShowWindow(localUrl);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Photino window failed");
                tcs.TrySetException(ex);
            }
        })
        {
            Name = "YuSwitch UI",
            IsBackground = false,
        };
        thread.Start();
        return tcs.Task;
    }

    private static void ShowWindow(string localUrl)
    {
        _localUrl = localUrl;
        var window = new PhotinoWindow()
            .SetTitle("禹枢 · AI 网关")
            .SetSize(1280, 800)
            .SetMinSize(900, 600)
            .Center();
        _window = window;

        // The native window exists once WindowCreated fires → install the
        // menu-bar item (hide/show needs the live NSWindow handle).
        window.RegisterWindowCreatedHandler((s, e) =>
        {
            if (!_helperAvailable) return;
            try
            {
                var handle = window.WindowHandle;
                if (handle != IntPtr.Zero)
                    YSInstallStatusItem(handle, _onOpenBrowserPtr, _onQuitPtr);
            }
            catch (Exception ex)
            {
                _helperAvailable = false;
                Log.Warning(ex, "Menu-bar helper unavailable; falling back to close = quit");
            }
        });

        // Close-to-menu-bar: the red button hides the window instead of quitting
        // (returning false cancels the close). The menu-bar 退出 sets _quitting
        // first, which lets this return true and the shell stop the server.
        window.RegisterWindowClosingHandler((s, e) =>
        {
            if (!_quitting && _helperAvailable)
            {
                try { YSHideWindow(window.WindowHandle); }
                catch { return true; }  // helper gone — let close proceed
                return false;
            }
            return true;
        });

        window.Load(localUrl);
        Log.Information("Photino window ready at {Url}", localUrl);
        window.WaitForClose();
    }

    private static void OpenInSystemBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("/usr/bin/open", url);
            else
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no default browser — nothing sensible to do */ }
    }

    /// <summary>Native macOS alert dialog via osascript (Windows uses MessageBox).</summary>
    private static void MacAlert(string title, string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo("/usr/bin/osascript")
            {
                ArgumentList = { "-e", $"display alert \"{title}\" message \"{message}\"" },
            });
        }
        catch { /* logging already captured the failure */ }
    }

    public void Dispose() { }
}
