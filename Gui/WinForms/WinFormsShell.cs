using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Serilog;

namespace YuSwitch.Gui;

/// <summary>
/// Windows desktop shell: WinForms + WebView2 window over the local gateway.
/// Owns single-instance detection (named mutex keyed by port), port pre-flight,
/// the WebView2 form (Gui.MainForm) and clean server shutdown when the tray
/// "退出" fires. The server is started/stopped here, inside RunAsync.
/// </summary>
public sealed class WinFormsShell : IShell
{
    public async Task RunAsync(WebApplication app, int port, CancellationToken ct)
    {
        var localUrl = $"http://localhost:{port}";

        // Single-instance guard, keyed by port. The OS releases the mutex if the
        // holding process dies, so "held" reliably means "instance alive".
        using var instanceMutex = new Mutex(initiallyOwned: true, $@"Global\YuSwitch-{port}", out var isFirstInstance);
        if (!isFirstInstance)
        {
            var running = await PortProbeService.ProbeAsync(localUrl) == PortProbe.Ours;
            MessageBox.Show(
                running
                    ? $"禹枢 已在运行（{localUrl}），将在浏览器中打开现有实例。"
                    : "禹枢 的另一个实例正在启动中，请稍候。",
                "禹枢",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            if (running)
                Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true });
            return;
        }

        // Mutex says we're the first YuSwitch on this port — but a FOREIGN
        // program could still hold the port (it wouldn't own our named mutex). If
        // something answers HTTP there that isn't us, prompt and bail WITHOUT
        // opening a browser, otherwise the WebView would silently load the foreign
        // app's page. (Non-HTTP occupants fall through to the StartAsync catch.)
        var preProbe = await PortProbeService.ProbeAsync(localUrl);
        if (preProbe == PortProbe.Foreign)
        {
            Log.Warning("Port {Port} is already in use by another program; aborting desktop startup", port);
            MessageBox.Show(
                $"端口 {port} 已被其他程序占用。\n\n" +
                "请在「设置 → 监听 / 网络」中修改监听端口，或关闭占用该端口的程序后重试。",
                "禹枢 启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        if (preProbe == PortProbe.Ours)
        {
            // Rare race: mutex free yet /health says ours. Treat like already-running.
            MessageBox.Show(
                $"禹枢 已在运行（{localUrl}），将在浏览器中打开现有实例。",
                "禹枢",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true });
            return;
        }

        // Start (and confirm) the server BEFORE showing any window, so a bind
        // failure is a visible error instead of a forever-"starting" shell.
        try
        {
            await app.StartAsync(ct);
            Log.Information("Gateway started at {Url} (desktop mode)", localUrl);
        }
        catch (Exception ex)
        {
            var baseEx = ex.GetBaseException();
            // Kestrel surfaces port conflicts as SocketException(AddressAlreadyInUse)
            // on some platforms and as IOException / a message on others; accept all
            // of them so the prompt is accurate. None of these paths open a browser.
            var msg = baseEx.Message ?? string.Empty;
            var portInUse = baseEx is System.Net.Sockets.SocketException
            {
                SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse
            }
            || baseEx is System.IO.IOException
            || msg.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("already in use", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("failed to bind", StringComparison.OrdinalIgnoreCase);
            var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Log.Fatal(ex, "Gateway failed to start");
            MessageBox.Show(
                (portInUse
                    ? $"端口 {port} 已被其他程序占用。请关闭占用该端口的程序，或在「设置 → 监听 / 网络」中修改监听端口。"
                    : $"启动失败：{baseEx.Message}")
                + $"\n\n详细日志：{logDir}",
                "禹枢 启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // WinForms + WebView2 require an STA thread. Program.cs's top-level main
        // runs on MTA thread-pool threads since the first await, so the message
        // loop gets its own explicitly-STA thread.
        var uiThread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new MainForm(port);
            Application.Run(form);
        })
        {
            Name = "YuSwitch UI",
            IsBackground = false,
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();

        // Window closed via tray "退出" → stop the server cleanly.
        await app.StopAsync();
    }

    public void Dispose() { }
}
