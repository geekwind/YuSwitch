using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using YuSwitch.Data;
using YuSwitch.Data.Entities;
using YuSwitch.Gateway;
using YuSwitch.Models;
using YuSwitch.Providers;
using YuSwitch.Services;

namespace YuSwitch.Endpoints;

/// <summary>
/// Admin REST API consumed by the Blazor UI. CRUD over services/models/keys,
/// connection test, usage stats, seed. All under /admin/*.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin");

        // Services
        g.MapGet("/services", ListServices);
        g.MapPost("/services", CreateService);
        g.MapPut("/services/{id}", UpdateService);
        g.MapPut("/services/{id}/enable", SetServiceEnabled);
        g.MapDelete("/services/{id}", DeleteService);
        g.MapPost("/services/{id}/test", TestService);
        g.MapPost("/services/{id}/discover-models", DiscoverModels);
        g.MapPost("/services/{id}/clone", CloneService);

        // Models
        g.MapGet("/services/{id}/models", ListModels);
        g.MapPost("/services/{id}/models", CreateModel);
        g.MapPut("/models/{id}", UpdateModel);
        g.MapDelete("/models/{id}", DeleteModel);

        // API keys
        g.MapGet("/apikeys", ListApiKeys);
        g.MapPost("/apikeys", CreateApiKey);
        g.MapPut("/apikeys/{id}", UpdateApiKey);
        g.MapDelete("/apikeys/{id}", DeleteApiKey);
        // One-click wiring of a local Claude Code / Codex CLI to this gateway:
        // detect install + config, back up the existing config, write ours.
        g.MapPost("/apikeys/{id}/wire", WireCli);

        // Config export / import (services + models + gateway keys)
        g.MapGet("/export", ExportConfig);
        g.MapPost("/import", ImportConfig);

        // Usage
        g.MapGet("/usage", GetUsage);
        g.MapGet("/usage/hourly", GetUsageHourly);
        g.MapGet("/usage/filters", GetUsageFilters);

        // Call logs (detailed per-request log with cache hit + token consumption)
        g.MapGet("/call-logs", GetCallLogs);

        // Dispatch trace (for sticky-session / failover observability)
        g.MapGet("/dispatch-trace", GetDispatchTrace);

        // Live per-service adaptive-LB state (in-flight, EWMA, breaker) for observability
        g.MapGet("/service-state", GetServiceState);

        // Provider types (for UI dropdown)
        g.MapGet("/provider-types", GetProviderTypes);

        // App settings (software name, subtitle, ...)
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", SaveSettings);

        // Restart the whole application (new process, re-reads listen_host/port).
        g.MapPost("/system/restart", RestartApplication);

        // Seed demo config
        g.MapPost("/seed", Seed);

        return app;
    }

    // --- Services ---

    /// <summary>Masked replacement kept stable so UpdateService can recognise
    /// "user didn't touch this credential" and keep the stored value.</summary>
    private const string MaskBody = "****";

    private static string MaskSecret(string v) =>
        string.IsNullOrEmpty(v) ? ""
        : v.Length <= 8 ? MaskBody
        : v[..3] + MaskBody + v[^4..];

    private static string MaskCredentialsJson(string json)
    {
        try
        {
            var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            return JsonSerializer.Serialize(creds.ToDictionary(kv => kv.Key, kv => MaskSecret(kv.Value)));
        }
        catch { return "{}"; }
    }

    private static bool LooksMasked(string v) => v.Contains(MaskBody);

    /// <summary>Merge incoming credentials over stored ones: masked/absent values
    /// keep the stored secret; only genuinely edited keys overwrite.</summary>
    private static string MergeCredentialsJson(string storedJson, string incomingJson)
    {
        Dictionary<string, string> stored, incoming;
        try { stored = JsonSerializer.Deserialize<Dictionary<string, string>>(storedJson) ?? new(); }
        catch { stored = new(); }
        try { incoming = JsonSerializer.Deserialize<Dictionary<string, string>>(incomingJson) ?? new(); }
        catch { return storedJson; }

        var merged = new Dictionary<string, string>();
        foreach (var (k, v) in incoming)
            merged[k] = LooksMasked(v) && stored.TryGetValue(k, out var orig) ? orig : v;
        return JsonSerializer.Serialize(merged);
    }

    private static async Task<IResult> ListServices(
        [FromServices] IDbContextFactory<AppDbContext> dbf)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var services = await db.Services.Include(s => s.Models).AsNoTracking().ToListAsync();
        // Never ship plaintext upstream keys to the browser: mask in place (the
        // entities are detached) and let UpdateService merge masked values back.
        foreach (var s in services)
            s.CredentialsJson = MaskCredentialsJson(s.CredentialsJson);
        return Results.Json(services, AdminJsonOpts);
    }

    /// <summary>Structured 400 for form validation, keyed per field so the UI
    /// can attach the message to its input: {errors:{name:"...",serverUrl:"..."}}.</summary>
    private static IResult ValidationError(Dictionary<string, string> errors) =>
        Results.Json(new { errors }, AdminJsonOpts, statusCode: 400);

    private static Dictionary<string, string> ValidateService(ServiceEntity svc)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(svc.Name))
            errors["name"] = "名称不能为空";
        if (string.IsNullOrWhiteSpace(svc.ProviderType))
            errors["providerType"] = "Provider 类型不能为空";
        if (string.IsNullOrWhiteSpace(svc.ServerUrl))
            errors["serverUrl"] = "上游地址不能为空";
        else if (!Uri.TryCreate(svc.ServerUrl.Trim(), UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors["serverUrl"] = "上游地址必须是合法的 http(s) URL";
        return errors;
    }

    private static async Task<IResult> CreateService(
        [FromBody] ServiceEntity svc, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        var errors = ValidateService(svc);
        if (errors.Count > 0) return ValidationError(errors);
        svc.Name = svc.Name.Trim();
        svc.ServerUrl = svc.ServerUrl.Trim();
        await using var db = await dbf.CreateDbContextAsync();
        db.Services.Add(svc);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        svc.CredentialsJson = MaskCredentialsJson(svc.CredentialsJson);
        return Results.Json(svc);
    }

    private static async Task<IResult> UpdateService(
        int id, [FromBody] ServiceEntity svc, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        var errors = ValidateService(svc);
        if (errors.Count > 0) return ValidationError(errors);
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.Services.FindAsync(id);
        if (existing is null) return Results.NotFound();
        existing.ProviderType = svc.ProviderType;
        existing.Name = svc.Name.Trim();
        existing.Enabled = svc.Enabled;
        existing.ServerUrl = svc.ServerUrl.Trim();
        existing.Weight = svc.Weight;
        existing.Priority = svc.Priority;
        // Masked credential values (from ListServices) mean "unchanged" — keep
        // the stored secret; only genuinely edited keys overwrite.
        existing.CredentialsJson = MergeCredentialsJson(existing.CredentialsJson, svc.CredentialsJson);
        existing.LimitJson = svc.LimitJson;
        existing.ModelRedirectJson = svc.ModelRedirectJson;
        existing.ModelMapJson = svc.ModelMapJson;
        existing.WebSearchJson = svc.WebSearchJson;
        existing.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        // Post-save, pre-response masking only touches the in-memory copy.
        existing.CredentialsJson = MaskCredentialsJson(existing.CredentialsJson);
        return Results.Json(existing);
    }

    /// <summary>Toggle a service's enabled flag without touching its other fields
    /// (handy for testing load balancing by isolating services).</summary>
    private static async Task<IResult> SetServiceEnabled(
        int id, [FromBody] System.Text.Json.JsonElement body,
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.Services.FindAsync(id);
        if (existing is null) return Results.NotFound();
        if (body.TryGetProperty("enabled", out var en))
            existing.Enabled = en.GetBoolean();
        existing.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(new { existing.Id, existing.Enabled });
    }

    private static async Task<IResult> DeleteService(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var svc = await db.Services.Include(s => s.Models).FirstOrDefaultAsync(s => s.Id == id);
        if (svc is null) return Results.NotFound();
        db.Services.Remove(svc);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Ok();
    }

    /// <summary>Duplicate a service together with all its models. The copy is
    /// created disabled so the user can adjust credentials before traffic hits it.</summary>
    private static async Task<IResult> CloneService(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var src = await db.Services.Include(s => s.Models).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);
        if (src is null) return Results.NotFound();

        var copy = new ServiceEntity
        {
            ProviderType = src.ProviderType,
            Name = src.Name + " (副本)",
            Enabled = false,
            ServerUrl = src.ServerUrl,
            Weight = src.Weight,
            Priority = src.Priority,
            CredentialsJson = src.CredentialsJson,
            LimitJson = src.LimitJson,
            ModelRedirectJson = src.ModelRedirectJson,
            ModelMapJson = src.ModelMapJson,
            WebSearchJson = src.WebSearchJson,
        };
        foreach (var m in src.Models)
        {
            copy.Models.Add(new ModelEntity
            {
                ModelName = m.ModelName,
                UpstreamModel = m.UpstreamModel,
                Aliases = m.Aliases,
                Enabled = m.Enabled,
                SupportsVision = m.SupportsVision,
                SupportsTools = m.SupportsTools,
                SupportsReasoning = m.SupportsReasoning,
                SupportsEmbeddings = m.SupportsEmbeddings,
            });
        }
        db.Services.Add(copy);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(new { ok = true, id = copy.Id, models = copy.Models.Count }, AdminJsonOpts);
    }

    // --- Models ---

    private static async Task<IResult> ListModels(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf) =>
        await QueryAsync(dbf, db => db.Models.Where(m => m.ServiceId == id).AsNoTracking().ToListAsync());

    private static async Task<IResult> CreateModel(
        int id, [FromBody] ModelEntity model, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        if (string.IsNullOrWhiteSpace(model.ModelName))
            return ValidationError(new() { ["modelName"] = "模型名称不能为空" });
        await using var db = await dbf.CreateDbContextAsync();
        model.ServiceId = id;
        model.ModelName = model.ModelName.Trim();
        db.Models.Add(model);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(model);
    }

    private static async Task<IResult> UpdateModel(
        int id, [FromBody] ModelEntity model, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.Models.FindAsync(id);
        if (existing is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(model.ModelName))
            return Results.BadRequest("ModelName is required");
        existing.ModelName = model.ModelName.Trim();
        existing.UpstreamModel = model.UpstreamModel?.Trim() ?? "";
        existing.Aliases = model.Aliases?.Trim() ?? "";
        existing.Enabled = model.Enabled;
        existing.SupportsVision = model.SupportsVision;
        existing.SupportsTools = model.SupportsTools;
        existing.SupportsReasoning = model.SupportsReasoning;
        existing.SupportsEmbeddings = model.SupportsEmbeddings;
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(existing);
    }

    private static async Task<IResult> DeleteModel(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var m = await db.Models.FindAsync(id);
        if (m is null) return Results.NotFound();
        db.Models.Remove(m);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Ok();
    }

    // --- API Keys ---

    private static async Task<IResult> ListApiKeys(
        [FromServices] IDbContextFactory<AppDbContext> dbf) =>
        await QueryAsync(dbf, db => db.ApiKeys.AsNoTracking().ToListAsync());

    private static async Task<IResult> CreateApiKey(
        [FromBody] ApiKeyEntity key, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        if (string.IsNullOrEmpty(key.KeyValue))
            key.KeyValue = "sk-" + Guid.NewGuid().ToString("N");
        await using var db = await dbf.CreateDbContextAsync();
        // Duplicate key values would make one of them unreachable and confuse
        // clients — refuse up front.
        if (await db.ApiKeys.AnyAsync(k => k.KeyValue == key.KeyValue))
            return ValidationError(new() { ["keyValue"] = "该 Key 值已存在" });
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(key);
    }

    /// <summary>Edit an existing gateway key in place (name / allowlist / enabled /
    /// key value) so rotating a permission doesn't force clients onto a new key.</summary>
    private static async Task<IResult> UpdateApiKey(
        int id, [FromBody] ApiKeyEntity key, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var existing = await db.ApiKeys.FindAsync(id);
        if (existing is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(key.KeyValue))
            return ValidationError(new() { ["keyValue"] = "Key 值不能为空" });
        if (await db.ApiKeys.AnyAsync(k => k.Id != id && k.KeyValue == key.KeyValue))
            return ValidationError(new() { ["keyValue"] = "该 Key 值已存在" });
        existing.Name = key.Name?.Trim() ?? "";
        existing.KeyValue = key.KeyValue.Trim();
        existing.AllowedModels = string.IsNullOrWhiteSpace(key.AllowedModels) ? "*" : key.AllowedModels.Trim();
        existing.Enabled = key.Enabled;
        existing.QpmLimit = Math.Max(0, key.QpmLimit);
        existing.DailyQuota = Math.Max(0, key.DailyQuota);
        existing.IpAllowlist = key.IpAllowlist?.Trim() ?? "";
        existing.ExpiresAt = key.ExpiresAt;
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(existing);
    }

    private static async Task<IResult> DeleteApiKey(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var k = await db.ApiKeys.FindAsync(id);
        if (k is null) return Results.NotFound();
        db.ApiKeys.Remove(k);
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Ok();
    }

    // --- One-click CLI wiring (Claude Code / Codex) ---

    /// <summary>Target CLI to wire.</summary>
    public enum WireTarget { Claude, Codex }

    /// <summary>Wires a local Claude Code or Codex CLI to use this gateway: detects
    /// the install + config file, backs up the existing config, and writes ours
    /// (gateway base URL + this key). Desktop app only — paths are the current
    /// user's home. Returns ok=false with a reason when the CLI isn't found.</summary>
    private static async Task<IResult> WireCli(
        int id, [FromQuery] WireTarget target,
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] AppSettingsService settings)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id);
        if (key is null) return Results.NotFound();
        if (!key.Enabled)
            return Results.Json(new WireResult(false, "该 Key 已停用，请先启用。"));

        // Gateway address the local CLI will call. Always localhost + the
        // configured listen port: Claude/Codex run on the same machine, so this
        // is reachable regardless of how the admin UI was accessed.
        var port = settings.ListenPort;
        var baseUrl = $"http://localhost:{port}";

        try
        {
            var (ok, message, detail) = target switch
            {
                WireTarget.Claude => await WireClaudeAsync(baseUrl, key.KeyValue),
                WireTarget.Codex => await WireCodexAsync(baseUrl, key.KeyValue),
                _ => (false, "未知的 target", (string?)null),
            };
            return Results.Json(new WireResult(ok, message) { Detail = detail });
        }
        catch (Exception ex)
        {
            return Results.Json(new WireResult(false, $"写入失败：{ex.Message}"));
        }
    }

    // Claude Code: ~/.claude/settings.json, env.ANTHROPIC_BASE_URL +
    // ANTHROPIC_AUTH_TOKEN. The gateway speaks the Anthropic /v1/messages API.
    private static async Task<(bool ok, string message, string? detail)> WireClaudeAsync(
        string baseUrl, string apiKey)
    {
        var exe = FindOnPath("claude");
        var claudeDir = Path.Combine(HomeDir(), ".claude");
        var settingsFile = Path.Combine(claudeDir, "settings.json");
        var dirExists = Directory.Exists(claudeDir);
        var fileExists = File.Exists(settingsFile);

        // Not installed at all → nothing to write to.
        if (string.IsNullOrEmpty(exe) && !dirExists)
            return (false, "未检测到 Claude Code：PATH 上没有 claude 命令，也没有 ~/.claude 目录。", null);

        Directory.CreateDirectory(claudeDir);

        // Back up the existing settings.json (and any prior backup) before writing.
        string? backup = null;
        if (fileExists)
            backup = BackupFile(settingsFile);

        // Merge into existing settings.json if present (preserve the user's other
        // keys), otherwise start fresh. env is set to point at the gateway.
        var json = new JsonObject();
        if (fileExists)
        {
            try { json = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(settingsFile)) ?? new JsonObject(); }
            catch { json = new JsonObject(); } // corrupt file → start fresh (already backed up)
        }
        // Merge our env vars into the existing env object (preserve the user's
        // other environment variables — only touch the two ANTHROPIC_* keys).
        var env = (json["env"] as JsonObject) ?? new JsonObject();
        json["env"] = env;
        env["ANTHROPIC_BASE_URL"] = baseUrl;
        // API_KEY (x-api-key) keeps Claude Code in plain API-key mode and avoids
        // fighting its OAuth/Anthropic-account login flow. The gateway accepts
        // x-api-key on /v1/messages.
        env["ANTHROPIC_API_KEY"] = apiKey;
        // Drop a stale AUTH_TOKEN from a prior (OAuth-style) wiring so it can't
        // override our API_KEY.
        env.Remove("ANTHROPIC_AUTH_TOKEN");
        await File.WriteAllTextAsync(settingsFile,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var note = exe is null
            ? "（提示：PATH 上未找到 claude 命令，仅写入了配置文件——确认 Claude Code 已安装。）"
            : null;
        var msg = $"已配置 Claude Code → {baseUrl}" +
                  (backup is null ? "（新建 settings.json）" : $"（已备份原配置：{Path.GetFileName(backup)}）") +
                  (note is null ? "" : note);
        return (true, msg, backup);
    }

    // Codex: ~/.codex/config.toml + ~/.codex/auth.json. Defines a new
    // model_providers.yuswitch and switches model_provider to it, and writes
    // the key into auth.json (OPENAI_API_KEY). wire_api=responses → /v1/responses.
    private static async Task<(bool ok, string message, string? detail)> WireCodexAsync(
        string baseUrl, string apiKey)
    {
        var exe = FindOnPath("codex");
        var codexDir = Path.Combine(HomeDir(), ".codex");
        var configFile = Path.Combine(codexDir, "config.toml");
        var authFile = Path.Combine(codexDir, "auth.json");
        var dirExists = Directory.Exists(codexDir);

        if (string.IsNullOrEmpty(exe) && !dirExists)
            return (false, "未检测到 Codex：PATH 上没有 codex 命令，也没有 ~/.codex 目录。", null);

        Directory.CreateDirectory(codexDir);

        // config.toml: replace any previously-written YuSwitch block with a
        // fresh one, and set model_provider to ours. We bracket our own lines with
        // marker comments so re-wiring deletes exactly that block (never the
        // user's other providers / mcp_servers / their own base_url). We DO remove
        // any top-level model_provider line so ours wins; other top-level keys
        // (model, reasoning effort, notify, ...) are left untouched.
        var toml = File.Exists(configFile) ? await File.ReadAllTextAsync(configFile) : "";
        var configBackup = File.Exists(configFile) ? BackupFile(configFile) : null;
        toml = StripYuSwitchBlock(toml);
        toml += "\n# --- YuSwitch start (written by the gateway admin UI) ---\n"
              + "model_provider = \"yuswitch\"\n"
              + "[model_providers.yuswitch]\n"
              + "name = \"YuSwitch\"\n"
              + $"base_url = \"{baseUrl}/v1\"\n"
              + "wire_api = \"responses\"\n"
              + "requires_openai_auth = true\n"
              + "# --- YuSwitch end ---\n";
        await File.WriteAllTextAsync(configFile, toml);

        // auth.json: merge OPENAI_API_KEY into whatever's there (preserve other
        // fields like a ChatGPT login's tokens), backing up first. Falls back to
        // a fresh object when the file is absent or not a JSON object.
        string? authBackup = null;
        if (File.Exists(authFile)) authBackup = BackupFile(authFile);
        var auth = new JsonObject { ["OPENAI_API_KEY"] = apiKey };
        if (File.Exists(authFile))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(authFile));
                if (existing is not null)
                {
                    foreach (var kv in existing)
                        if (!auth.ContainsKey(kv.Key)) auth[kv.Key] = kv.Value?.DeepClone();
                }
            }
            catch { /* not a JSON object → start fresh (already backed up) */ }
        }
        await File.WriteAllTextAsync(authFile,
            auth.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var note = exe is null
            ? "（提示：PATH 上未找到 codex 命令，仅写入了配置文件——确认 Codex 已安装。）"
            : null;
        var msg = $"已配置 Codex → {baseUrl}/v1（responses）" +
                  (configBackup is null ? "" : $"，已备份 config.toml（{Path.GetFileName(configBackup)}）") +
                  (authBackup is null ? "" : $"、auth.json（{Path.GetFileName(authBackup)}）") +
                  (note is null ? "" : note);
        return (true, msg, authBackup ?? configBackup);
    }

    /// <summary>Removes the previously-written YuSwitch block (everything
    /// between the start/end marker comments, inclusive) and any top-level
    /// model_provider line, so re-wiring replaces cleanly without stacking.
    /// Everything else in the file is left byte-for-byte intact.</summary>
    private static string StripYuSwitchBlock(string toml)
    {
        // Drop our marked block. The markers are only ever written by us, so this
        // can't touch user content.
        var start = "# --- YuSwitch start";
        var end = "# --- YuSwitch end ---";
        var sb = new System.Text.StringBuilder(toml.Length);
        var lines = toml.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith(start, StringComparison.Ordinal))
            {
                // Skip until the matching end marker (inclusive).
                while (i < lines.Length && !lines[i].Trim().StartsWith(end, StringComparison.Ordinal))
                    i++;
                continue; // i now at the end marker (or past end); loop's i++ moves on
            }
            // Also drop any top-level model_provider line that isn't inside a
            // [section] — ours lives inside our block (already removed), but a
            // leftover from an older wiring (pre-markers) could still be here.
            if (t.StartsWith("model_provider", StringComparison.Ordinal) &&
                (t.Contains('=')) &&
                // Only treat as top-level: a crude check that the value is a bare
                // string (not a table). Safe because model_provider is always top-level.
                t.Contains('"'))
            {
                continue;
            }
            sb.AppendLine(lines[i]);
        }
        return sb.ToString().Replace("\r\n", "\n").TrimEnd() + "\n";
    }

    /// <summary>Copy file to &lt;name&gt;.bak-yyyymmddHHmmss (next to it). No-op-safe.</summary>
    private static string BackupFile(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var bak = $"{path}.bak-{stamp}";
        File.Copy(path, bak, overwrite: false);
        return bak;
    }

    /// <summary>Look for an executable on PATH (Windows: PATHEXT-aware). Returns
    /// the full path or null.</summary>
    private static string? FindOnPath(string name)
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            var exts = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".exe;.cmd;.bat").Split(';')
                : new[] { "" };
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in exts)
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { /* best effort */ }
        return null;
    }

    private static string HomeDir() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public class WireResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string? Detail { get; set; }
        public WireResult(bool ok, string message) { Ok = ok; Message = message; }
    }

    // --- Export / import ---

    /// <summary>Full config backup: services (with plaintext credentials — /admin
    /// is auth-guarded), models, and gateway keys. For migration between machines.</summary>
    private static async Task<IResult> ExportConfig(
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] AppSettingsService settings)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var services = await db.Services.Include(s => s.Models).AsNoTracking().ToListAsync();
        var keys = await db.ApiKeys.AsNoTracking().ToListAsync();
        var payload = new
        {
            app = "YuSwitch",
            exportedAt = DateTimeOffset.Now,
            services = services.Select(s => new
            {
                s.ProviderType, s.Name, s.Enabled, s.ServerUrl, s.Weight, s.Priority,
                s.CredentialsJson, s.LimitJson, s.ModelRedirectJson, s.ModelMapJson, s.WebSearchJson,
                models = s.Models.Select(m => new
                {
                    m.ModelName, m.UpstreamModel, m.Aliases, m.Enabled,
                    m.SupportsVision, m.SupportsTools, m.SupportsReasoning, m.SupportsEmbeddings,
                }),
            }),
            apiKeys = keys.Select(k => new { k.KeyValue, k.Name, k.Enabled, k.AllowedModels }),
        };
        return Results.Json(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }

    /// <summary>Import a config exported by ExportConfig. Additive: services are
    /// matched by name (existing ones are skipped, not overwritten), keys by value.</summary>
    private static async Task<IResult> ImportConfig(
        [FromBody] JsonElement body,
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        int svcAdded = 0, svcSkipped = 0, keyAdded = 0, keySkipped = 0;

        if (body.TryGetProperty("services", out var services) && services.ValueKind == JsonValueKind.Array)
        {
            var existingNames = (await db.Services.Select(s => s.Name).ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var s in services.EnumerateArray())
            {
                var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0 || existingNames.Contains(name)) { svcSkipped++; continue; }
                var svc = new ServiceEntity
                {
                    Name = name,
                    ProviderType = GetStr(s, "providerType", "openai"),
                    Enabled = GetBool(s, "enabled"),
                    ServerUrl = GetStr(s, "serverUrl", ""),
                    Weight = GetInt(s, "weight", 1),
                    Priority = GetInt(s, "priority", 0),
                    CredentialsJson = GetStr(s, "credentialsJson", "{}"),
                    LimitJson = GetStr(s, "limitJson", "{}"),
                    ModelRedirectJson = GetStr(s, "modelRedirectJson", "{}"),
                    ModelMapJson = GetStr(s, "modelMapJson", "{}"),
                    WebSearchJson = GetStr(s, "webSearchJson", "{}"),
                };
                if (s.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        var mn = GetStr(m, "modelName", "");
                        if (mn.Length == 0) continue;
                        svc.Models.Add(new ModelEntity
                        {
                            ModelName = mn,
                            UpstreamModel = GetStr(m, "upstreamModel", ""),
                            Aliases = GetStr(m, "aliases", ""),
                            Enabled = GetBool(m, "enabled"),
                            SupportsVision = GetBool(m, "supportsVision"),
                            SupportsTools = GetBool(m, "supportsTools"),
                            SupportsReasoning = GetBool(m, "supportsReasoning"),
                            SupportsEmbeddings = GetBool(m, "supportsEmbeddings"),
                        });
                    }
                }
                db.Services.Add(svc);
                existingNames.Add(name);
                svcAdded++;
            }
        }

        if (body.TryGetProperty("apiKeys", out var apiKeys) && apiKeys.ValueKind == JsonValueKind.Array)
        {
            var existingKeys = (await db.ApiKeys.Select(k => k.KeyValue).ToListAsync()).ToHashSet();
            foreach (var k in apiKeys.EnumerateArray())
            {
                var kv = GetStr(k, "keyValue", "");
                if (kv.Length == 0 || existingKeys.Contains(kv)) { keySkipped++; continue; }
                db.ApiKeys.Add(new ApiKeyEntity
                {
                    KeyValue = kv,
                    Name = GetStr(k, "name", ""),
                    Enabled = GetBool(k, "enabled"),
                    AllowedModels = GetStr(k, "allowedModels", "*"),
                });
                existingKeys.Add(kv);
                keyAdded++;
            }
        }

        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(new
        {
            ok = true,
            services = new { added = svcAdded, skipped = svcSkipped },
            apiKeys = new { added = keyAdded, skipped = keySkipped },
        }, AdminJsonOpts);
    }

    private static string GetStr(JsonElement e, string prop, string fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static bool GetBool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;
    private static int GetInt(JsonElement e, string prop, int fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    // --- Usage / test / seed ---

    private static async Task<IResult> GetUsage(
        [FromQuery] int? hours, [FromQuery] string? model, [FromQuery] string? service,
        [FromQuery] string? provider, [FromServices] UsageService usage, CancellationToken ct) =>
        Results.Json(await usage.GetStatsAsync(NormHours(hours), model, service, provider, ct));

    private static async Task<IResult> GetUsageHourly(
        [FromQuery] int? hours, [FromQuery] string? model, [FromQuery] string? service,
        [FromQuery] string? provider, [FromServices] UsageService usage, CancellationToken ct) =>
        Results.Json(await usage.GetHourlyAsync(NormHours(hours), model, service, provider, ct));

    private static async Task<IResult> GetUsageFilters(
        [FromServices] UsageService usage, CancellationToken ct) =>
        Results.Json(await usage.GetFilterOptionsAsync(ct));

    private static int NormHours(int? h) => h is > 0 and <= 24 * 90 ? h.Value : 24;

    private static IResult GetDispatchTrace() =>
        Results.Json(GatewayService.DispatchTraces, AdminJsonOpts);

    private static IResult GetServiceState([FromServices] GatewayService gw) =>
        Results.Json(gw.ServiceStates(), AdminJsonOpts);

    private static async Task<IResult> GetCallLogs(
        [FromQuery] int limit,
        [FromQuery] int offset,
        [FromQuery] string? model,
        [FromQuery] string? service,
        [FromQuery] string? provider,
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        CancellationToken ct)
    {
        var lim = limit <= 0 || limit > 500 ? 50 : limit;
        var off = Math.Max(0, offset);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var qr = db.UsageLogs.AsNoTracking() as IQueryable<UsageLogEntity>;
        if (!string.IsNullOrWhiteSpace(model)) qr = qr.Where(l => l.Model == model);
        if (!string.IsNullOrWhiteSpace(service)) qr = qr.Where(l => l.ServiceName == service);
        if (!string.IsNullOrWhiteSpace(provider)) qr = qr.Where(l => l.ProviderType == provider);
        if (status == "success") qr = qr.Where(l => l.Success);
        else if (status == "failed") qr = qr.Where(l => !l.Success);
        if (!string.IsNullOrWhiteSpace(q))
            qr = qr.Where(l => l.Model.Contains(q) || l.UpstreamModel.Contains(q)
                || l.ServiceName.Contains(q) || l.PromptPreview.Contains(q) || l.ResponsePreview.Contains(q));
        var total = await qr.CountAsync(ct);
        var logs = await qr.OrderByDescending(l => l.Timestamp).Skip(off).Take(lim).ToListAsync(ct);
        return Results.Json(new
        {
            total,
            offset = off,
            limit = lim,
            items = logs.Select(l => new
            {
                timestamp = l.Timestamp,
                model = l.Model,
                upstreamModel = l.UpstreamModel,
                service = l.ServiceName,
                provider = l.ProviderType,
                apiKey = l.ApiKeyName,
                success = l.Success,
                statusCode = l.StatusCode,
                promptTokens = l.PromptTokens,
                completionTokens = l.CompletionTokens,
                totalTokens = l.TotalTokens,
                reasoningTokens = l.ReasoningTokens,
                cacheCreationTokens = l.CacheCreationTokens,
                cacheReadTokens = l.CacheReadTokens,
                cacheHit = l.CacheHit,
                latencyMs = l.LatencyMs,
                ttftMs = l.TtftMs,
                stream = l.Stream,
                sessionId = string.IsNullOrEmpty(l.SessionId) ? null : l.SessionId,
                promptPreview = l.PromptPreview,
                responsePreview = l.ResponsePreview,
                error = string.IsNullOrEmpty(l.Error) ? null : l.Error,
            }),
        }, AdminJsonOpts);
    }

    private static async Task<IResult> GetProviderTypes(
        [FromServices] IProviderRegistry registry) =>
        Results.Json(registry.RegisteredTypes);

    // --- Settings ---

    private static IResult GetSettings([FromServices] AppSettingsService settings) =>
        Results.Json(new Dictionary<string, string>
        {
            [AppSettingsService.KeyAppName] = settings.AppName,
            [AppSettingsService.KeySubtitle] = settings.Subtitle,
            [AppSettingsService.KeyLogoType] = settings.LogoType,
            [AppSettingsService.KeyLogoValue] = settings.LogoValue,
            // listen_host/port take effect only after a restart, but return the
            // stored value so the UI can show what's currently configured.
            [AppSettingsService.KeyListenHost] = settings.ListenHost,
            [AppSettingsService.KeyListenPort] = settings.ListenPort.ToString(),
            // Never return the admin token itself — only a masked marker so the
            // UI can show "set/not set". SaveSettings skips masked values.
            [AppSettingsService.KeyAdminToken] = MaskSecret(settings.AdminToken),
            // Gateway-side web search global key — masked like the admin token so
            // the UI shows "set/not set" but never the plaintext.
            [AppSettingsService.KeyWebSearchTavilyKey] = MaskSecret(settings.WebSearchTavilyKey),
            [AppSettingsService.KeyRequestTimeoutDefaultS] = settings.RequestTimeoutDefaultS.ToString(),
            [AppSettingsService.KeyHealthProbeEnabled] = settings.HealthProbeEnabled ? "true" : "false",
            [AppSettingsService.KeyHealthProbeIntervalS] = settings.HealthProbeIntervalS.ToString(),
            // Adaptive LB / circuit-breaker / rate-limit tunables (hot-reloaded).
            [AppSettingsService.KeyLbEwmaAlpha] = settings.LbEwmaAlpha.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AppSettingsService.KeyBreakerFailureThreshold] = settings.BreakerFailureThreshold.ToString(),
            [AppSettingsService.KeyBreakerCooldownBaseS] = settings.BreakerCooldownBaseS.ToString(),
            [AppSettingsService.KeyBreaker429PenaltyMs] = settings.Breaker429PenaltyMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AppSettingsService.KeyBreaker429WindowS] = settings.Breaker429WindowS.ToString(),
            [AppSettingsService.KeyInFlightPenaltyMs] = settings.InFlightPenaltyMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AppSettingsService.KeyEwmaDecayS] = settings.EwmaDecayS.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AppSettingsService.KeyLbStickyFactor] = settings.LbStickyFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AppSettingsService.KeyRateLimitEnabled] = settings.RateLimitEnabled ? "true" : "false",
            [AppSettingsService.KeyUsageLogRetentionDays] = settings.UsageLogRetentionDays.ToString(),
        }, AdminJsonOpts);

    private static async Task<IResult> SaveSettings(
        [FromBody] Dictionary<string, string> incoming,
        [FromServices] AppSettingsService settings,
        CancellationToken ct)
    {
        foreach (var key in new[]
        {
            AppSettingsService.KeyAppName,
            AppSettingsService.KeySubtitle,
            AppSettingsService.KeyLogoType,
            AppSettingsService.KeyLogoValue,
            AppSettingsService.KeyListenHost,
            AppSettingsService.KeyListenPort,
            AppSettingsService.KeyAdminToken,
            AppSettingsService.KeyWebSearchTavilyKey,
            AppSettingsService.KeyRequestTimeoutDefaultS,
            AppSettingsService.KeyHealthProbeEnabled,
            AppSettingsService.KeyHealthProbeIntervalS,
            // Adaptive LB / circuit-breaker / rate-limit tunables + retention.
            AppSettingsService.KeyLbEwmaAlpha,
            AppSettingsService.KeyBreakerFailureThreshold,
            AppSettingsService.KeyBreakerCooldownBaseS,
            AppSettingsService.KeyBreaker429PenaltyMs,
            AppSettingsService.KeyBreaker429WindowS,
            AppSettingsService.KeyInFlightPenaltyMs,
            AppSettingsService.KeyEwmaDecayS,
            AppSettingsService.KeyLbStickyFactor,
            AppSettingsService.KeyRateLimitEnabled,
            AppSettingsService.KeyUsageLogRetentionDays,
        })
            if (incoming.TryGetValue(key, out var v))
            {
                // A masked value round-tripped from GetSettings means "unchanged" —
                // don't overwrite the real secret with the mask. Applies to the
                // admin token and the global Tavily key.
                if ((key == AppSettingsService.KeyAdminToken ||
                     key == AppSettingsService.KeyWebSearchTavilyKey) && LooksMasked(v ?? ""))
                    continue;
                await settings.SetAsync(key, v?.Trim() ?? "", ct);
            }
        return Results.Json(new { ok = true }, AdminJsonOpts);
    }

    /// <summary>
    /// Restarts the whole application: spawns a fresh process carrying
    /// <c>--restart-of &lt;pid&gt;</c> (which waits for THIS process to exit so the
    /// port/mutex are released), then stops and exits the current one. Desktop
    /// mode relaunches the GUI with the new settings; a container without a
    /// restart policy just stops (rely on docker/systemd/k8s to restart it).
    /// </summary>
    private static IResult RestartApplication([FromServices] IHostApplicationLifetime life)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return Results.Json(new
            {
                ok = false,
                error = "无法确定可执行文件路径；容器/托管环境请依赖 restart 策略（docker restart=always / k8s / systemd）重启。"
            }, AdminJsonOpts);
        }

        try
        {
            // Environment.ProcessPath is our app exe for a published/self-contained
            // launch, but the `dotnet` host when launched via `dotnet run` or
            // `dotnet YuSwitch.dll`. Detect the host and, in the latter case,
            // relaunch as `dotnet <dll> --restart-of <pid>` so dev launches
            // restart correctly too. (Single-file publish has an empty assembly
            // Location, but isDotnetHost is false there so we never need it.)
            var hostName = Path.GetFileName(exe);
            var isDotnetHost = hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                || hostName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (isDotnetHost)
            {
                // Entry assembly = our app dll. Empty under single-file publish,
                // but isDotnetHost is false there so this branch never runs.
                var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(dll))
                    return Results.Json(new { ok = false, error = "无法确定应用程序集路径以重启。" }, AdminJsonOpts);
                psi.ArgumentList.Add(dll);
            }
            psi.ArgumentList.Add("--restart-of");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = "拉起新进程失败：" + ex.Message }, AdminJsonOpts);
        }

        // Fire-and-forget: let the HTTP response flush, then stop the host and
        // force-exit. StopApplication alone won't end the WinForms message pump
        // in desktop mode, so Environment.Exit guarantees the old process dies
        // (releasing the port/mutex the new process is waiting on).
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            try { life.StopApplication(); } catch { /* best effort */ }
            await Task.Delay(300);
            Environment.Exit(0);
        });

        return Results.Json(new { ok = true, message = "正在重启，请稍候…" }, AdminJsonOpts);
    }

    private static async Task<IResult> DiscoverModels(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] IProviderRegistry registry,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var svc = await db.Services.Include(s => s.Models).FirstOrDefaultAsync(s => s.Id == id);
        if (svc is null) return Results.NotFound();

        // Build provider and check if it supports model listing.
        var provider = registry.Create(svc);
        if (provider is not IModelListable listable)
            return Results.Json(new { ok = false, reason = "not_supported", message = "该 Provider 类型不支持模型列举" }, AdminJsonOpts);

        List<UpstreamModelInfo> models;
        // The upstream HttpClient has no timeout of its own — bound this admin
        // call locally so a hung upstream can't wedge the request forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { models = await listable.ListModelsAsync(cts.Token); }
        catch (OperationCanceledException) { return Results.Json(new { ok = false, reason = "timeout", message = "上游超时（25 秒无响应）" }, AdminJsonOpts); }
        catch (Exception ex) { return Results.Json(new { ok = false, reason = "upstream_error", message = ex.Message }, AdminJsonOpts); }

        // Dedup against existing models on this service (case-insensitive by ModelName).
        var existing = svc.Models.Select(m => m.ModelName.ToLowerInvariant()).ToHashSet();
        var imported = new List<string>();
        var skipped = new List<string>();
        foreach (var m in models)
        {
            if (string.IsNullOrEmpty(m.Id)) continue;
            var name = m.Id;
            if (existing.Contains(name.ToLowerInvariant()))
            { skipped.Add(name); continue; }
            db.Models.Add(new ModelEntity
            {
                ServiceId = id,
                ModelName = name,
                UpstreamModel = name,
                Aliases = m.DisplayName ?? "",
                Enabled = true,
                SupportsTools = true,
                SupportsVision = false,
                SupportsReasoning = false,
                SupportsEmbeddings = false,
            });
            existing.Add(name.ToLowerInvariant());
            imported.Add(name);
        }
        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Json(new { ok = true, imported, skipped, total = models.Count }, AdminJsonOpts);
    }

    private static async Task<IResult> TestService(
        int id, [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] IProviderRegistry registry)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var svc = await db.Services.Include(s => s.Models).FirstOrDefaultAsync(s => s.Id == id);
        if (svc is null) return Results.NotFound();
        try
        {
            var provider = registry.Create(svc);
            var mdl = svc.Models.FirstOrDefault();
            var modelName = mdl?.ResolveUpstreamModel() ?? "gpt-3.5-turbo";
            var req = new ChatRequest
            {
                Model = modelName,
                ClientModel = mdl?.ModelName ?? modelName,
                MaxTokens = 5,
                Messages = new() { new() { Role = "user", Content = "hi" } },
            };
            // The upstream HttpClient has no timeout — bound the probe locally.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var resp = await provider.ChatAsync(req, cts.Token);
            return Results.Json(new { ok = true, model = resp.Model });
        }
        catch (OperationCanceledException)
        {
            return Results.Json(new { ok = false, error = "上游超时（25 秒无响应）" });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message });
        }
    }

    private static async Task<IResult> Seed(
        [FromServices] IDbContextFactory<AppDbContext> dbf,
        [FromServices] ConfigService config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Services.AnyAsync())
            return Results.Ok(new { seeded = false, reason = "already has services" });

        // Create a placeholder service — user must fill in their own upstream URL
        // and API key via the admin UI before use. No real credentials here.
        var svc = new ServiceEntity
        {
            ProviderType = "openai",
            Name = "My Upstream (configure me)",
            Enabled = false,
            ServerUrl = "https://api.example.com/v1",
            CredentialsJson = """{"api_key":"your-api-key-here"}""",
        };
        db.Services.Add(svc);
        await db.SaveChangesAsync();

        // A few common model names as examples (user should adjust to match
        // their upstream's actual model list, or use "发现模型" to auto-discover).
        foreach (var m in new[]
        {
            ("gpt-4o", true, true, false, false),
            ("gpt-4o-mini", true, true, false, false),
            ("claude-3-5-sonnet-latest", true, true, false, false),
        })
        {
            db.Models.Add(new ModelEntity
            {
                ServiceId = svc.Id,
                ModelName = m.Item1,
                Enabled = true,
                SupportsVision = m.Item2,
                SupportsTools = m.Item3,
                SupportsReasoning = m.Item4,
                SupportsEmbeddings = m.Item5,
            });
        }

        db.ApiKeys.Add(new ApiKeyEntity
        {
            KeyValue = "sk-yuswitch-local",
            Name = "local",
            Enabled = true,
            AllowedModels = "*",
        });

        await db.SaveChangesAsync();
        await config.ReloadAsync();
        return Results.Ok(new { seeded = true, services = 1, models = 3, keys = 1 });
    }

    // --- helpers ---

    private static readonly JsonSerializerOptions AdminJsonOpts = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task<IResult> QueryAsync<T>(
        IDbContextFactory<AppDbContext> dbf, Func<AppDbContext, Task<List<T>>> query) =>
        Results.Json(await query(await dbf.CreateDbContextAsync()), AdminJsonOpts);
}
