using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace YuSwitch.Gui;

/// <summary>
/// WinForms main form hosting a WebView2 control. The ASP.NET Core gateway
/// runs on a background thread; this form provides the desktop shell.
/// </summary>
public class MainForm : Form
{
    private const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const int MaxNavigationRetries = 10;

    private readonly WebView2 _webView;
    private readonly Label _loadingLabel;
    private readonly Button _retryButton;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _retryTimer;
    private readonly int _port;
    private readonly string _homeUrl;
    private int _navigationRetries;
    private bool _initialLoadSucceeded;
    private bool _reallyClose;
    private readonly Icon _appIcon;

    public MainForm(int port)
    {
        _port = port;
        _homeUrl = $"http://localhost:{port}";

        Text = "禹枢 · AI 网关";
        Width = 1280;
        Height = 800;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        _appIcon = LoadAppIcon() ?? SystemIcons.Application;
        Icon = _appIcon;

        // Loading overlay shown until the gateway page finishes loading.
        _loadingLabel = new Label
        {
            Text = "正在启动 禹枢，请稍候…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 14),
            ForeColor = Color.FromArgb(80, 80, 80),
            BackColor = Color.White,
        };
        Controls.Add(_loadingLabel);

        _retryButton = new Button
        {
            Text = "重试",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Visible = false,
        };
        _retryButton.Click += (_, _) =>
        {
            _retryButton.Visible = false;
            _navigationRetries = 0;
            _loadingLabel.Text = "正在重试…";
            NavigateHome();
        };
        Controls.Add(_retryButton);
        _retryButton.Location = new Point((ClientSize.Width - _retryButton.Width) / 2,
                                          ClientSize.Height / 2 + 40);

        // WebView2 fills the form (kept hidden until first successful navigation).
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = false,
        };
        Controls.Add(_webView);
        _loadingLabel.BringToFront();
        _retryButton.BringToFront();

        // Retry navigation on the UI thread; the timer dies with the form, so
        // no callbacks can fire against a disposed control.
        _retryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer.Stop();
            NavigateHome();
        };

        // Tray icon.
        _trayIcon = new NotifyIcon
        {
            Text = "禹枢 AI 网关",
            Visible = true,
            Icon = _appIcon,
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开界面", null, (_, _) => ShowFromTray());
        menu.Items.Add("在浏览器中打开", null, (_, _) => OpenInSystemBrowser());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _reallyClose = true;
            Close();
        });
        _trayIcon.ContextMenuStrip = menu;

        // Initialize WebView2 once the form has loaded — by then the message
        // pump is running and the WindowsFormsSynchronizationContext is
        // installed, so async continuations resume on the UI thread. Doing it
        // earlier (before Application.Run) silently breaks navigation.
        Load += OnFormLoad;
    }

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        // WebView2 Evergreen Runtime is a separate install and may be absent
        // (LTSC / stripped-down Windows). Degrade instead of dying quietly.
        if (!IsWebView2RuntimeAvailable())
        {
            var choice = MessageBox.Show(this,
                "未检测到 WebView2 运行时（显示界面所需）。\n\n" +
                "是（Y）：打开 WebView2 运行时下载页面\n" +
                "否（N）：改用系统浏览器打开管理界面（网关保持运行）",
                "禹枢", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice == DialogResult.Yes)
                OpenUrl(RuntimeDownloadUrl);
            else
                OpenInSystemBrowser();
            _loadingLabel.Text = $"网关运行中：{_homeUrl}\n可从托盘图标打开或退出。";
            return;
        }

        try
        {
            await InitWebViewAsync();
        }
        catch (Exception ex)
        {
            _loadingLabel.Text = "界面初始化失败：" + ex.Message + $"\n网关仍在运行：{_homeUrl}";
            _retryButton.Visible = false;
        }
    }

    public async Task InitWebViewAsync()
    {
        // Use a stable user-data folder under LocalAppData: single-file
        // publish runs from a temp extraction dir, and pointing WebView2 at
        // a writable, persistent location avoids init/permission failures.
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YuSwitch", "WebView2");
        Directory.CreateDirectory(userDataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await _webView.EnsureCoreWebView2Async(env);

        // target=_blank links (external docs etc.) go to the system browser
        // instead of spawning bare popup windows.
        _webView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenUrl(args.Uri);
        };

        _webView.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                _initialLoadSucceeded = true;
                _webView.Visible = true;
                _loadingLabel.Visible = false;
                _retryButton.Visible = false;
                return;
            }

            // Only babysit the FIRST load. Later failures (user-cancelled
            // navigation, downloads, external links) must not yank the user
            // back to the home page.
            if (_initialLoadSucceeded)
                return;

            _loadingLabel.Visible = true;
            _webView.Visible = false;
            if (++_navigationRetries > MaxNavigationRetries)
            {
                _loadingLabel.Text = $"无法加载管理界面（{_homeUrl}）。\n请检查日志（logs 目录）后重试。";
                _retryButton.Visible = true;
                return;
            }
            _loadingLabel.Text = args.HttpStatusCode != 0
                ? $"加载失败（HTTP {args.HttpStatusCode}），即将重试（{_navigationRetries}/{MaxNavigationRetries}）…"
                : $"网关尚未就绪，正在重试（{_navigationRetries}/{MaxNavigationRetries}）…";
            _retryTimer.Start();
        };

        await WaitForGatewayAsync();
        NavigateHome();
    }

    private void NavigateHome()
    {
        if (IsDisposed || _webView.CoreWebView2 is null) return;
        _webView.CoreWebView2.Navigate(_homeUrl);
    }

    // The server is confirmed started before this form is even created; this
    // only covers the tiny window between StartAsync returning and Kestrel
    // actually serving.
    private async Task WaitForGatewayAsync()
    {
        using var http = new HttpClient();
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var resp = await http.GetAsync($"{_homeUrl}/health");
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not up yet */ }
            await Task.Delay(200);
        }
    }

    private void OpenInSystemBrowser() => OpenUrl(_homeUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no default browser — nothing sensible to do */ }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClose && (e.CloseReason == CloseReason.UserClosing ||
                              e.CloseReason == CloseReason.FormOwnerClosing))
        {
            // Minimize to tray instead of closing.
            e.Cancel = true;
            Hide();
            return;
        }

        _trayIcon.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _retryTimer.Dispose();
            _trayIcon.Dispose();
            if (_appIcon is not null && _appIcon != SystemIcons.Application)
                _appIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Load the app icon embedded with the exe (used for the window
    /// title bar and the tray).</summary>
    private static Icon? LoadAppIcon()
    {
        var asm = typeof(MainForm).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("icon.ico", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        return stream is null ? null : new Icon(stream);
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

}
