using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YuSwitch.Services;

namespace YuSwitch.Middleware;

/// <summary>
/// Guards the whole management surface — /admin/*, the Blazor UI pages and the
/// /_blazor circuit — against remote access. Gateway API paths (/v1 etc.) are
/// exempt (ApiKeyAuthMiddleware owns those) and so is /health.
///
/// - Loopback requests always pass: the zero-friction single-user desktop
///   experience. This also covers the server's own Blazor-circuit HttpClient,
///   which calls /admin back over localhost.
/// - Remote requests with no admin token configured are rejected (fail
///   closed) with a hint to set a token from localhost first.
/// - Remote requests with a token configured must present it via the
///   X-Admin-Token header, Authorization: Bearer, ?admin_token= query, or the
///   eg_admin cookie. The query form is a one-shot bootstrap that plants the
///   cookie, so browser navigation and the SignalR circuit then carry it.
///
/// Deliberately lightweight — a shared secret for a single-user tool, not
/// multi-user auth. Public exposure should still sit behind a TLS proxy.
/// </summary>
public class AdminAuthMiddleware
{
    public const string CookieName = "eg_admin";
    public const string HeaderName = "X-Admin-Token";
    public const string QueryName = "admin_token";

    private readonly RequestDelegate _next;
    private readonly AppSettingsService _settings;

    public AdminAuthMiddleware(RequestDelegate next, AppSettingsService settings)
    {
        _next = next; _settings = settings;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";

        // Gateway API and health probes are not admin surface.
        if (IsExemptPath(path)) { await _next(ctx); return; }

        // Cross-site guard (drive-by localhost / CSRF). A page open in the
        // operator's browser on ANOTHER origin must never reach the admin
        // surface — even though its requests come from loopback (the browser
        // is local, so the loopback bypass below would otherwise wave them
        // through). Browsers mark such requests Sec-Fetch-Site: cross-site;
        // older ones send an Origin whose host differs from ours. Requests
        // with neither header (curl, scripts, same-origin navigation) pass.
        if (IsCrossSiteBrowserRequest(ctx))
        {
            await Reject(ctx, 403, path, "已拒绝跨站请求：管理界面不接受来自其他网站的浏览器请求。");
            return;
        }

        // Loopback = the local user (or the server calling itself). Zero friction.
        if (IsLoopback(ctx)) { await _next(ctx); return; }

        var token = _settings.AdminToken;
        if (string.IsNullOrEmpty(token))
        {
            await Reject(ctx, 403, path,
                "远程访问已禁用：请先在本机打开管理界面，在「设置」中配置管理令牌 (admin token)。");
            return;
        }

        var supplied = ctx.Request.Headers[HeaderName].FirstOrDefault()
            ?? BearerToken(ctx.Request.Headers.Authorization.ToString())
            ?? (ctx.Request.Query.TryGetValue(QueryName, out var q) ? q.ToString() : null)
            ?? ctx.Request.Cookies[CookieName];

        if (string.IsNullOrEmpty(supplied) || !FixedTimeEquals(supplied, token))
        {
            await Reject(ctx, 401, path,
                "需要管理令牌：在 URL 后附加 ?admin_token=<令牌>，或携带 X-Admin-Token 请求头。");
            return;
        }

        // Valid token via query → plant the cookie so subsequent navigation and
        // the SignalR circuit authenticate without the query string.
        if (ctx.Request.Query.ContainsKey(QueryName) && ctx.Request.Cookies[CookieName] != supplied)
        {
            ctx.Response.Cookies.Append(CookieName, supplied, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                // HTTPS (TLS-terminating proxy) → Secure; plain HTTP dev → omit
                // or the browser would drop the cookie entirely.
                Secure = ctx.Request.IsHttps,
            });
        }

        await _next(ctx);
    }

    // /v1*, /chat/completions, /messages, /responses are the gateway data
    // plane (ApiKeyAuthMiddleware gates them); /health serves probes.
    private static bool IsExemptPath(string path) =>
        path.StartsWith("/v1/") || path.StartsWith("/v1beta/") ||
        path is "/chat/completions" or "/messages" or "/responses" ||
        path.StartsWith("/health");

    private static bool IsLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip &&
        (IPAddress.IsLoopback(ip) || (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())));

    /// <summary>True for browser requests initiated by a different site:
    /// Sec-Fetch-Site: cross-site (all modern browsers), or an Origin header
    /// whose host:port isn't the request's own Host. "same-site"/"same-origin"
    /// and header-less requests (curl, scripts) are not browser cross-site.</summary>
    private static bool IsCrossSiteBrowserRequest(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("Sec-Fetch-Site", out var sfs) &&
            sfs.ToString().Equals("cross-site", StringComparison.OrdinalIgnoreCase))
            return true;

        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var o)
            && !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string? BearerToken(string auth) =>
        auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : null;

    /// <summary>Constant-time comparison so the token can't be guessed byte-by-byte.</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task Reject(HttpContext ctx, int status, string path, string message)
    {
        ctx.Response.StatusCode = status;
        if (path.StartsWith("/admin"))
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
        }
        else
        {
            // Browser navigation → a minimal readable page instead of raw JSON.
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(
                $"<!doctype html><html><head><meta charset=\"utf-8\"><title>YuSwitch</title></head>" +
                $"<body style=\"font-family:system-ui;max-width:40em;margin:4em auto;line-height:1.6\">" +
                $"<h2>访问受限</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
        }
    }
}
