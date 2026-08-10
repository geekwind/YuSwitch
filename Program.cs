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
// Self-update control flags (spawned by UpdateService's staged new binary).
// --install-self <stageDir> <installAt>: this process is the freshly downloaded
// binary; after waiting for the old PID, swap ourselves over the real exe and
// relaunch it. --cleanup-stage <dir>: best-effort remove the leftover staging
// dir after startup. All are stripped from the args the relaunched process sees.
bool installSelf = false;
string? updateStageDir = null;
string? updateInstallAt = null;
string? updateCleanupStage = null;
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
    if (a.Equals("--install-self", StringComparison.OrdinalIgnoreCase)) { installSelf = true; continue; }
    if (a.Equals("--stage", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { updateStageDir = args[i + 1]; i++; continue; }
    if (a.Equals("--install-at", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { updateInstallAt = args[i + 1]; i++; continue; }
    if (a.Equals("--cleanup-stage", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { updateCleanupStage = args[i + 1]; i++; continue; }
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

// --- Self-update: swap the staged new binary over the running exe and relaunch.
// Runs BEFORE any EF/config/GUI/port-bind init: the old process (waited on
// above) has exited, so the real exe is unlocked and the port/mutex are free.
// Windows locks a running exe, so we Copy ourselves (source is read-shared)
// onto the real path; POSIX allows an atomic Move (the running process keeps
// its old inode). On any failure, relaunch the real path unchanged so the app
// still comes back up (a plain restart) rather than lingering from the stage. ---
if (installSelf)
{
    if (string.IsNullOrEmpty(updateInstallAt) || string.IsNullOrEmpty(updateStageDir))
    {
        Log.Error("Self-update: missing install info (at={At}, stage={Stage}); falling back to restart", updateInstallAt, updateStageDir);
    }
    else
    {
        // The headless/--no-gui switches were stripped from filteredArgs (a bare
        // switch would otherwise swallow the next arg in CreateBuilder), so re-add
        // --headless when we came from a headless run to preserve the original mode.
        var relaunchArgs = forceHeadless
            ? filteredArgs.Append("--headless").ToArray()
            : filteredArgs;
        try
        {
            if (OperatingSystem.IsWindows())
                File.Copy(Environment.ProcessPath!, updateInstallAt, overwrite: true);
            else
                File.Move(Environment.ProcessPath!, updateInstallAt, overwrite: true);
            Log.Information("Self-update: replaced {Path}, relaunching new binary", updateInstallAt);
            StartRelaunch(updateInstallAt, relaunchArgs, updateStageDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Self-update swap failed; falling back to plain restart");
            try { StartRelaunch(updateInstallAt, relaunchArgs, null); }
            catch (Exception ex2) { Log.Error(ex2, "Self-update fallback restart also failed"); }
        }
        Environment.Exit(0);
    }
}

// Best-effort cleanup of the leftover staging dir. The staging process exits
// right after spawning us, but on Windows its exe file stays locked until that
// process fully tears down, so a single attempt can race it — retry briefly.
if (!string.IsNullOrEmpty(updateCleanupStage))
{
    var toClean = updateCleanupStage;
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(toClean))
                    Directory.Delete(toClean, true);
                return;
            }
            catch { /* stage dir still locked; retry */ }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    });
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
builder.Services.AddSingleton<UpdateService>();
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

// --- HTTP client for self-update (GitHub Releases API + asset downloads).
// Check requests are short (20s); large asset downloads get a per-instance
// longer timeout in UpdateService. Auto-redirect (default) follows the CDN. ---
builder.Services.AddHttpClient("github", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("YuSwitch-updater");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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

    // --- DB self-heal pre-flight (小白-friendly) ---
    // Runs on its own short-lived connection BEFORE EF opens/migrates anything:
    //  1. existing healthy DB → rolling backup (SQLite online BackupDatabase,
    //     consistent even in WAL mode) so a failed upgrade can be rolled back by
    //     swapping the newest simpleone.db.bak-* back in;
    //  2. corrupt DB → auto-restore the newest backup;
    //  3. corrupt with no backup, or a migration failure (below) → stop with a
    //     clear message instead of a silent crash / half-migrated DB.
    var dbFile = ResolveDbFile(builder.Configuration["Database:Path"] ?? "simpleone.db");
    if (dbFile is not null && File.Exists(dbFile))
    {
        if (!await DbIntegrityOkAsync(dbFile))
        {
            Log.Error("Database integrity check FAILED on {Path}; attempting restore from newest backup", dbFile);
            var restoreError = TryRestoreNewestBackup(dbFile);
            if (restoreError is not null || !await DbIntegrityOkAsync(dbFile))
                FatalDbError("数据库已损坏，且无法自动恢复："
                    + (restoreError ?? "恢复后的文件仍然损坏。"));
            else
                Log.Information("Database restored from newest backup after integrity failure");
        }
        else
        {
            var backupFile = await CreateDbBackupAsync(dbFile);
            if (backupFile is not null)
                Log.Information("Database backed up to {Backup}", backupFile);
            PruneDbBackups(dbFile, keep: 5);
        }
    }

    await using var db = await dbf.CreateDbContextAsync();
    try
    {
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
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Database migration failed; stopping startup to avoid a half-migrated DB");
        FatalDbError("数据库升级失败，为避免数据损坏已停止启动。"
            + "可把目录下最新一份 simpleone.db.bak-* 改名为 simpleone.db 后重启；"
            + "或删除 simpleone.db 让应用重建（会丢失全部配置）。");
        return;
    }

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

// Launches the (already swapped-in) real exe. CWD is the exe's install dir so
// the relaunched process starts in the right place; its Program.cs top re-pins
// to GetAppDataDir() anyway. args = original command line minus internal flags.
static void StartRelaunch(string exe, string[] args, string? cleanupStage)
{
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = exe,
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    if (cleanupStage is not null)
    {
        psi.ArgumentList.Add("--cleanup-stage");
        psi.ArgumentList.Add(cleanupStage);
    }
    System.Diagnostics.Process.Start(psi);
}

// Absolute path of the SQLite file for the configured Database:Path, or null
// for in-memory / non-file connection strings.
static string? ResolveDbFile(string dbPath)
{
    if (dbPath.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        return null;
    return Path.GetFullPath(dbPath);
}

// PRAGMA integrity_check on a short-lived standalone connection. Unopenable or
// any non-"ok" result counts as unhealthy.
static async Task<bool> DbIntegrityOkAsync(string dbFile)
{
    try
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var res = await cmd.ExecuteScalarAsync();
        return string.Equals(Convert.ToString(res), "ok", StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not open database for integrity check on {Path}", dbFile);
        return false;
    }
}

// Rolling backup of a healthy DB via SQLite's online backup API — a consistent
// snapshot even under WAL. Timestamped name; the newest *.bak-* is the restore
// point. Best effort: a failed backup only logs a warning.
static async Task<string?> CreateDbBackupAsync(string dbFile)
{
    try
    {
        var dest = $"{dbFile}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        await using var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}");
        await source.OpenAsync();
        await using var destConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dest}");
        await destConn.OpenAsync();
        source.BackupDatabase(destConn); // runs to completion synchronously
        return dest;
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Database backup failed; continuing without a snapshot");
        return null;
    }
}

// Replaces the (corrupt) DB with the newest timestamped backup. Deletes any
// stale WAL/SHM sidecars first, else SQLite could replay old journal data over
// the restored file. Returns null on success, or a user-facing error message.
static string? TryRestoreNewestBackup(string dbFile)
{
    try
    {
        var dir = Path.GetDirectoryName(dbFile) ?? ".";
        var pattern = Path.GetFileName(dbFile) + ".bak-*";
        var newest = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (newest is null)
            return "未找到任何备份文件。";
        File.Copy(newest, dbFile, overwrite: true);
        foreach (var ext in new[] { "-wal", "-shm" })
        {
            var side = dbFile + ext;
            if (File.Exists(side)) File.Delete(side);
        }
        return null;
    }
    catch (Exception ex)
    {
        return "恢复失败：" + ex.Message;
    }
}

// Keeps only the newest `keep` backups; older ones are deleted.
static void PruneDbBackups(string dbFile, int keep)
{
    try
    {
        var dir = Path.GetDirectoryName(dbFile) ?? ".";
        var pattern = Path.GetFileName(dbFile) + ".bak-*";
        foreach (var old in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
            .Skip(keep))
        {
            File.Delete(old);
        }
    }
    catch { /* best effort */ }
}

// Friendly fatal error for a DB problem: always logs loudly; on an interactive
// Windows desktop launch also pops a message box. Ends the process (exit 1).
static void FatalDbError(string detail)
{
    Log.Error("Fatal database error: {Detail}", detail);
#if WINDOWS
    if (OperatingSystem.IsWindows() && Environment.UserInteractive)
    {
        System.Windows.Forms.MessageBox.Show(
            "YuSwitch 启动失败：数据库异常。\n\n" + detail + "\n\n（详见 logs/easy-gateway-.log）",
            "YuSwitch",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error);
    }
#endif
    Environment.Exit(1);
}

