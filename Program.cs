using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Serilog;
using YuSwitch.Components;
using YuSwitch.Data;
using YuSwitch.Endpoints;
using YuSwitch.Gateway;
using YuSwitch.Middleware;
using YuSwitch.Providers;
using YuSwitch.Providers.Claude;
using YuSwitch.Providers.OpenAI;
using YuSwitch.Services;

// Single-file publish extracts to a temp folder and runs from there, so
// relative paths (logs/, simpleone.db) would be wiped on exit. Pin the
// working directory to a stable, user-writable location so state persists:
//  - macOS .app bundle → ~/Library/Application Support/YuSwitch (the bundle
//    itself must stay read-only and movable);
//  - everything else → the real exe location.
try
{
    var appDataDir = GetAppDataDir();
    if (appDataDir is not null &&
        !string.Equals(Directory.GetCurrentDirectory(), appDataDir, StringComparison.OrdinalIgnoreCase))
    {
        Directory.CreateDirectory(appDataDir);
        Directory.SetCurrentDirectory(appDataDir);
    }
}
catch { /* best effort */ }

// GUI/headless is decided from the raw args, then the switches are stripped
// before reaching CreateBuilder: its command-line configuration source treats
// a bare "--headless" as a key and swallows the NEXT argument as its value
// ("--headless --urls http://..." would crash with FormatException).
var forceHeadless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase)
                               || a.Equals("--no-gui", StringComparison.OrdinalIgnoreCase));

// --restart-of <pid> is spawned by POST /admin/system/restart. The new process
// waits (below) for the old PID to fully exit so the port/mutex are released,
// then boots fresh with the new settings. Strip BOTH the flag and its value,
// the same way a bare switch would otherwise swallow the next argument.
int? restartOfPid = null;
var filtered = new List<string>(args.Length);
for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (a.Equals("--headless", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("--no-gui", StringComparison.OrdinalIgnoreCase))
        continue;
    if (a.Equals("--restart-of", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[i + 1], out var pid)) restartOfPid = pid;
        i++; // consume the value
        continue;
    }
    filtered.Add(a);
}
var filteredArgs = filtered.ToArray();

var builder = WebApplication.CreateBuilder(filteredArgs);

// Static web assets (incl. build-generated scoped CSS like
// YuSwitch.styles.css) come from the default pipeline. This call loads the
// manifest when present and is a no-op otherwise, so running straight from
// bin/ in Production also picks up scoped CSS.
StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);

// --- Serilog ---
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/easy-gateway-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();
builder.Host.UseSerilog();

// If this process is a restart child, wait for the previous process to fully
// exit before anything that needs the port/mutex (the single-instance guard
// and Kestrel bind both run later). Bounded so a hung old process can't block
// us forever.
if (restartOfPid is int prevPid)
{
    Log.Information("Restart: waiting for previous process {Pid} to exit", prevPid);
    WaitForProcessExit(prevPid, TimeSpan.FromSeconds(30));
    Log.Information("Previous process exited; continuing startup");
}

// --- EF Core (SQLite default, zero-config) ---
var dbPath = builder.Configuration["Database:Path"] ?? "simpleone.db";
builder.Services.AddDbContextFactory<AppDbContext>(opt =>
    opt.UseSqlite($"Data Source={dbPath}"));

// --- Config + usage services ---
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<UsageService>();
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<AppVersionService>();
builder.Services.AddSingleton<ToastService>();
builder.Services.AddSingleton<ApiKeyLimiter>();
builder.Services.AddHostedService<HealthProbeService>();

// --- Real-time notifications for dashboard auto-refresh ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<RealtimeNotificationService>();

// --- HTTP client for all upstream providers ---
// No client-level timeout and NO transparent retry: the gateway's own
// failover/breaker (GatewayService) owns both decisions. A hidden Polly
// retry would burn ~7s inside one bad upstream before failover could act,
// and blindly retry non-idempotent POSTs. Every caller MUST bring its own
// CancellationToken bound (GatewayService per-service timeout; admin
// test/discover use a short local CTS).
builder.Services.AddHttpClient("openai", c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
});

// --- HTTP client for gateway-side web search (Tavily). Bounded timeout: the
// search is an additive enrichment step, so it must not hang the request. ---
builder.Services.AddHttpClient("tavily", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});

// --- CORS (open gateway: allow any origin for the API endpoints) ---
builder.Services.AddCors(o => o.AddPolicy("Any", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// --- Provider registry ---
builder.Services.AddSingleton<IProviderRegistry, ProviderRegistry>();
builder.Services.AddSingleton(sp => (ProviderRegistry)sp.GetRequiredService<IProviderRegistry>());

// --- Gateway-side web search (Tavily) ---
builder.Services.AddSingleton<WebSearchService>();

// --- Gateway ---
builder.Services.AddSingleton<GatewayService>();

// --- Blazor ---
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// HttpClient for Blazor components. NavigationManager resolves in both the
// prerender scope and the interactive circuit scope (where HttpContext is
// null), and its BaseUri reflects the actual host/port the browser used.
// The admin token (when set) rides along as a default header: this client is
// the server calling itself, so reading the setting directly is simplest.
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    var http = new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
    var token = sp.GetRequiredService<AppSettingsService>().AdminToken;
    if (!string.IsNullOrEmpty(token))
        http.DefaultRequestHeaders.Add(AdminAuthMiddleware.HeaderName, token);
    return http;
});

var app = builder.Build();

// Register provider factories now that registry exists.
var registry = app.Services.GetRequiredService<ProviderRegistry>();
registry.Register("openai", (svc, sp) =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new OpenAIProvider(httpFactory.CreateClient("openai"), svc);
});
registry.Register("deepseek", (svc, sp) =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new OpenAIProvider(httpFactory.CreateClient("openai"), svc);
});
registry.Register("zhipu", (svc, sp) =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new OpenAIProvider(httpFactory.CreateClient("openai"), svc);
});
registry.Register("groq", (svc, sp) =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new OpenAIProvider(httpFactory.CreateClient("openai"), svc);
});
registry.Register("claude", (svc, sp) =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new ClaudeProvider(httpFactory.CreateClient("openai"), svc);
});
registry.Register("anthropic", (svc, sp) =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new ClaudeProvider(httpFactory.CreateClient("openai"), svc);
});
// Generic "upstream" type for passthrough OpenAI-compatible services.
registry.Register("upstream", (svc, sp) =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new OpenAIProvider(httpFactory.CreateClient("openai"), svc);
});

// --- Database init ---
// Read listen_host/listen_port here (settings reload just below) so the bind
// decision further down can use them.
var listenHost = AppSettingsService.DefaultListenHost;
var listenPort = AppSettingsService.DefaultListenPort;
using (var scope = app.Services.CreateScope())
{
    var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbf.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    // EnsureCreated only builds the schema on a fresh DB; for an existing DB it
    // won't add tables introduced later. Create the Settings table idempotently.
    await db.Database.ExecuteSqlRawAsync(
        """CREATE TABLE IF NOT EXISTS "Settings" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "Key" TEXT NOT NULL, "Value" TEXT NOT NULL);""");
    await db.Database.ExecuteSqlRawAsync(
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Settings_Key" ON "Settings" ("Key");""");
    // Index usage logs by timestamp for time-bucketed dashboards (no-op on
    // fresh DBs, which already get the index via the EF model configuration).
    await db.Database.ExecuteSqlRawAsync(
        """CREATE INDEX IF NOT EXISTS "IX_UsageLogs_Timestamp" ON "UsageLogs" ("Timestamp");""");
    // Add UpstreamModel to existing UsageLogs tables (no-op on fresh DBs).
    await AddColumnIfMissingAsync(db, "UsageLogs", "UpstreamModel", "TEXT NOT NULL DEFAULT ''");
    // Cache/reasoning usage columns added later — migrate older DBs that
    // predate them, else SaveChangesAsync throws "no such column" and the
    // background drainer silently drops the whole log row. Fresh DBs (EnsureCreated)
    // already have them, so these are no-ops there.
    await AddColumnIfMissingAsync(db, "UsageLogs", "ReasoningTokens", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UsageLogs", "CacheCreationTokens", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UsageLogs", "CacheReadTokens", "INTEGER NOT NULL DEFAULT 0");
    await AddColumnIfMissingAsync(db, "UsageLogs", "CacheHit", "INTEGER NOT NULL DEFAULT 0");
    // Per-service web search config column (idempotent; fresh DBs get it via
    // the EF model, older DBs via ALTER).
    await AddColumnIfMissingAsync(db, "Services", "WebSearchJson", "TEXT NOT NULL DEFAULT '{}'");
    var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
    await config.ReloadAsync();
    var appSettings = scope.ServiceProvider.GetRequiredService<AppSettingsService>();
    await appSettings.ReloadAsync();
    listenHost = appSettings.ListenHost;
    listenPort = appSettings.ListenPort;
    Log.Information("Config loaded: {Services} services, {Models} models, {Keys} keys",
        config.Snapshot.Services.Count, config.Snapshot.Models.Count, config.Snapshot.ApiKeys.Count);
    Log.Information("Listen endpoint: http://{Host}:{Port} (change in Settings → 监听/网络, restart to apply)",
        listenHost, listenPort);
}

// --- Middleware pipeline ---
app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

// Default static-files pipeline (static web assets in dev, wwwroot next to
// the exe when published). When there is no wwwroot on disk at all (single
// exe copied around alone), fall back to the assets embedded at build time.
app.UseStaticFiles();
if (!Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot"),
    });
}
app.UseAntiforgery();

// Permissive CORS (open gateway). Placed BEFORE the auth middleware so that
// cross-origin preflight (OPTIONS) is answered by the CORS layer with the
// Access-Control-Allow-* headers instead of being rejected with a 401 — the
// preflight carries no credentials, so it can never pass the API-key check.
app.UseCors("Any");

// Admin auth (loopback zero-friction / token when set) then gateway API auth.
app.UseMiddleware<AdminAuthMiddleware>();
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.MapOpenAiEndpoints();
app.MapAnthropicEndpoints();
app.MapResponsesEndpoints();

// Health endpoints for k8s/docker probes. "app"/"version" also let the
// desktop shell recognize an already-running instance on the same port.
var appVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
app.MapGet("/health", () => Results.Json(new { status = "ok", app = "YuSwitch", version = appVersion, timestamp = DateTimeOffset.Now }));
app.MapGet("/health/ready", () => Results.Json(new { status = "ok" }));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Admin API for Blazor UI
app.MapAdminEndpoints();

// Resolve the listen endpoint. Precedence: --urls / ASPNETCORE_URLS / Urls
// (Docker/CI) first; otherwise the DB-backed listen_host/listen_port settings.
// BindingAddress (unlike Uri) accepts the "+"/"*" wildcard hosts used in
// containers. When `urls` is provided and valid, the framework already applies
// it to app.Urls — we only set app.Urls explicitly when nothing usable was given.
var port = listenPort;
var nonLoopback = false;
var desktopMode = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
    && Environment.UserInteractive && !forceHeadless;
var urls = builder.Configuration["Urls"];
if (!string.IsNullOrEmpty(urls))
{
    try
    {
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var addr = BindingAddress.Parse(first);
        if (addr.Port > 0) port = addr.Port;
        nonLoopback = !IsLoopbackHost(addr.Host);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not parse Urls {Urls}; falling back to {Host}:{Port}", urls, listenHost, port);
        urls = null!; // drop through to the explicit bind below
    }
}

if (string.IsNullOrEmpty(urls))
{
    app.Urls.Clear();
    var host = string.IsNullOrWhiteSpace(listenHost) ? AppSettingsService.DefaultListenHost : listenHost.Trim();
    app.Urls.Add($"http://{host}:{port}");
    nonLoopback = !IsLoopbackHost(host);

    // The desktop shell (WebView2) and tray "open in browser" always hit
    // localhost:<port>. When the user binds a SPECIFIC remote IP, also bind
    // localhost so the local shell keeps working. 0.0.0.0/+ and localhost
    // already cover loopback, so this only adds a URL for the specific-IP case.
    if (desktopMode && nonLoopback && host is not ("0.0.0.0" or "+" or "*"))
        app.Urls.Add($"http://localhost:{port}");
}

if (nonLoopback)
{
    var hasAdminToken = !string.IsNullOrEmpty(
        app.Services.GetRequiredService<AppSettingsService>().AdminToken);
    if (hasAdminToken)
        Log.Information("Listening on a non-local address; /admin is protected by the admin token");
    else
        Log.Warning("Listening on a non-local address with no admin token — remote /admin requests will be rejected until a token is set in Settings");
}

// --- Desktop GUI mode ---
// Windows runs the WinForms/WebView2 shell; macOS runs the Photino (WKWebView)
// shell. Both are thin wrappers over the same local gateway — the shell starts
// the server and stops it on exit. Headless mode (servers, Docker, Linux,
// --headless) falls through to app.Run() below.
if (desktopMode)
{
    using var shell = YuSwitch.Gui.ShellFactory.Create();
    if (shell is not null)
    {
        await shell.RunAsync(app, port, CancellationToken.None);
        return;
    }
}

// Headless mode (server/Docker, or --headless on Windows/macOS).
app.Run();

// Adds a column to an existing table if it isn't there yet. SQLite has no
// IF NOT EXISTS for ADD COLUMN, so probe pragma_table_info first.
static async Task AddColumnIfMissingAsync(AppDbContext db, string table, string column, string definition)
{
    await using var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();
    await using (var check = conn.CreateCommand())
    {
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
        var exists = Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;
        if (exists) return;
    }
    await using var alter = conn.CreateCommand();
    alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
    await alter.ExecuteNonQueryAsync();
    Log.Information("Migrated: added column {Table}.{Column}", table, column);
}

// True for loopback bind hosts (localhost / 127.0.0.1 / ::1). Everything else
// (0.0.0.0 / + / a specific IP) means remotely reachable.
static bool IsLoopbackHost(string? host) =>
    host is null
    || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
    || host.Equals("127.0.0.1")
    || host.Equals("[::1]")
    || host.Equals("::1");

// Stable directory for runtime state (simpleone.db, logs/). Inside a macOS
// .app bundle the executable lives in a read-only, movable bundle, so state
// goes to ~/Library/Application Support/YuSwitch instead of next to the binary.
static string? GetAppDataDir()
{
    var exePath = Environment.ProcessPath;
    if (string.IsNullOrEmpty(exePath)) return null;
    if (OperatingSystem.IsMacOS() &&
        exePath.Contains("/Contents/MacOS/", StringComparison.OrdinalIgnoreCase))
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Library", "Application Support", "YuSwitch");
    }
    var exeDir = Path.GetDirectoryName(exePath);
    return string.IsNullOrEmpty(exeDir) ? null : exeDir;
}

// Blocks until the given PID is gone or the timeout elapses. Used by the
// restart child to wait for the old process to release its port/mutex before
// booting, so the new instance doesn't trip the single-instance guard.
static void WaitForProcessExit(int pid, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try { _ = System.Diagnostics.Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }                  // no such process
        catch (System.ComponentModel.Win32Exception) { return; } // access denied / gone
        Thread.Sleep(300);
    }
}

