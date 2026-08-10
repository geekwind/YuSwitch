using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace YuSwitch.Services;

/// <summary>
/// Self-update for the published single-file binary. The admin Settings page
/// calls <see cref="CheckAsync"/> (query GitHub Releases /latest for a newer
/// version and the platform-matching asset), then <see cref="ApplyAsync"/>:
/// download the archive, verify its .sha256, extract the single binary to a
/// staging dir, and spawn it with --install-self so Program.cs swaps it over
/// the running exe and restarts (see the restart-of machinery it reuses).
/// </summary>
public class UpdateService
{
    private const string DefaultLatestUrl = "https://api.github.com/repos/geekwind/YuSwitch/releases/latest";

    private const string FlagRestartOf = "--restart-of";
    private const string FlagInstallSelf = "--install-self";
    private const string FlagStage = "--stage";
    private const string FlagInstallAt = "--install-at";
    private const string FlagCleanupStage = "--cleanup-stage";

    private readonly IHttpClientFactory _http;
    private readonly AppSettingsService _settings;
    private readonly AppVersionService _ver;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public UpdateService(IHttpClientFactory http, AppSettingsService settings, AppVersionService ver)
    {
        _http = http;
        _settings = settings;
        _ver = ver;
    }

    public record UpdateCheckInfo(
        string Current, string Latest, bool Available, bool Supported, string Platform,
        string AssetFileName, string AssetUrl, string ChecksumUrl, long AssetSizeBytes,
        string ReleaseNotes, string Message);

    public async Task<UpdateCheckInfo> CheckAsync(CancellationToken ct)
    {
        var current = _ver.Version;
        var platform = DetectRid();

        // Inside a macOS .app bundle the executable lives in a read-only,
        // movable bundle — swapping it in place is not supported.
        if (OperatingSystem.IsMacOS()
            && Environment.ProcessPath is { } p
            && p.Contains("/Contents/MacOS/", StringComparison.OrdinalIgnoreCase))
            return new(current, "", false, false, platform, "", "", "", 0, "",
                "macOS .app 包装形态暂不支持自动更新，请手动下载新版本（.dmg 或 headless tar.gz）。");

        var latestUrl = _settings.UpdateBaseUrl;
        if (string.IsNullOrWhiteSpace(latestUrl)) latestUrl = DefaultLatestUrl;

        string json;
        try
        {
            using var client = _http.CreateClient("github");
            using var resp = await client.GetAsync(latestUrl, ct);
            json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new(current, "", false, true, platform, "", "", "", 0, "",
                    $"检查更新失败：HTTP {(int)resp.StatusCode}"
                    + (resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? "（GitHub 未鉴权接口限流 60 次/时，请稍后再试）。" : "。"));
        }
        catch (Exception ex)
        {
            return new(current, "", false, true, platform, "", "", "", 0, "", $"检查更新失败：{ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var latestTag = GetString(root, "tag_name");
            var latest = latestTag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? latestTag[1..] : latestTag;
            var notes = GetString(root, "body");

            var ext = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
            var prefix = $"YuSwitch-{platform}-v";
            var assetName = ""; var assetUrl = ""; long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = GetString(a, "name");
                    if (string.IsNullOrEmpty(name)
                        || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        continue;
                    assetName = name;
                    assetUrl = GetString(a, "browser_download_url");
                    if (a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number) size = s.GetInt64();
                    break;
                }
            }

            if (string.IsNullOrEmpty(assetUrl))
                return new(current, latest, false, true, platform, "", "", "", 0, notes,
                    $"未找到 {platform} 平台的更新资产（{ext}）。");

            var available = Version.TryParse(latest, out var lv)
                && Version.TryParse(current, out var cv)
                && lv > cv;
            var message = available
                ? $"发现新版本 v{latest}（当前 v{current}）。"
                : $"已是最新版本 v{current}。";
            return new(current, latest, available, true, platform,
                assetName, assetUrl, assetUrl + ".sha256", size, notes, message);
        }
        catch (Exception ex)
        {
            return new(current, "", false, true, platform, "", "", "", 0, "", $"解析更新信息失败：{ex.Message}");
        }
    }

    public async Task<(bool Ok, string Error)> ApplyAsync(CancellationToken ct)
    {
        if (!await _applyLock.WaitAsync(0, ct))
            return (false, "已有更新任务正在进行中，请稍候。");

        try
        {
            // dotnet host = running via `dotnet run` / `dotnet YuSwitch.dll`
            // (single-file publish has Environment.ProcessPath = the real exe).
            var hostName = Path.GetFileName(Environment.ProcessPath ?? "");
            if (hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                || hostName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return (false, "当前为 dotnet 开发态（非发布后的单文件），无法自动更新。请使用发布版二进制。");

            var info = await CheckAsync(ct);
            if (!info.Available)
                return (false, string.IsNullOrEmpty(info.Message) ? "当前已是最新版本。" : info.Message);

            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(exeDir))
                return (false, "无法确定可执行文件目录。");

            // Stage under the real exe's dir so the swap is same-volume.
            var updateRoot = Path.Combine(exeDir, ".update");
            Directory.CreateDirectory(updateRoot);
            var stageDir = Path.Combine(updateRoot, "stage");
            TryDeleteDir(stageDir);
            var archivePath = Path.Combine(updateRoot, info.AssetFileName);
            var checksumPath = archivePath + ".sha256";

            try
            {
                using (var client = _http.CreateClient("github"))
                {
                    client.Timeout = TimeSpan.FromMinutes(5); // large self-contained binary
                    await DownloadToFileAsync(client, info.AssetUrl, archivePath, ct);
                    await DownloadToFileAsync(client, info.ChecksumUrl, checksumPath, ct);
                }

                var shaText = await File.ReadAllTextAsync(checksumPath, ct);
                var tokens = shaText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    return (false, "校验文件内容为空，已中止更新。");

                byte[] actual;
                await using (var fs = File.OpenRead(archivePath))
                    actual = await SHA256.HashDataAsync(fs, ct);
                if (!Convert.ToHexString(actual).Equals(tokens[0], StringComparison.OrdinalIgnoreCase))
                    return (false, "下载文件校验失败（SHA256 不匹配），已中止更新。请重试或手动更新。");

                Directory.CreateDirectory(stageDir);
                if (OperatingSystem.IsWindows())
                {
                    ZipFile.ExtractToDirectory(archivePath, stageDir, overwriteFiles: true);
                }
                else
                {
                    await using var gz = File.OpenRead(archivePath);
                    using var unzip = new GZipStream(gz, CompressionMode.Decompress);
                    TarFile.ExtractToDirectory(unzip, stageDir, overwriteFiles: true);
                }

                var binName = OperatingSystem.IsWindows() ? "YuSwitch.exe" : "YuSwitch";
                var stageExe = Path.Combine(stageDir, binName);
                if (!File.Exists(stageExe))
                    return (false, $"压缩包内未找到 {binName}，已中止更新。");

                var psi = new ProcessStartInfo
                {
                    FileName = stageExe,
                    UseShellExecute = false,
                    WorkingDirectory = exeDir,
                };
                foreach (var arg in BuildStageLaunchArgs(stageDir, Environment.ProcessPath!))
                    psi.ArgumentList.Add(arg);
                Process.Start(psi);

                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, $"更新失败：{ex.Message}");
            }
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>Reconstruct the original command line minus internal control
    /// flags, then append the flags that make Program.cs do the swap.</summary>
    private static IEnumerable<string> BuildStageLaunchArgs(string stageDir, string installAt)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals(FlagInstallSelf, StringComparison.OrdinalIgnoreCase))
                continue;
            if (a.Equals(FlagRestartOf, StringComparison.OrdinalIgnoreCase)
                || a.Equals(FlagStage, StringComparison.OrdinalIgnoreCase)
                || a.Equals(FlagInstallAt, StringComparison.OrdinalIgnoreCase)
                || a.Equals(FlagCleanupStage, StringComparison.OrdinalIgnoreCase))
            {
                i++; // consume the flag's value
                continue;
            }
            yield return a;
        }
        yield return FlagRestartOf;
        yield return Environment.ProcessId.ToString();
        yield return FlagInstallSelf;
        yield return FlagStage;
        yield return stageDir;
        yield return FlagInstallAt;
        yield return installAt;
    }

    private static async Task DownloadToFileAsync(HttpClient client, string url, string path, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载失败：HTTP {(int)resp.StatusCode}（{url}）");
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await src.CopyToAsync(dst, ct);
    }

    private static string DetectRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "unknown",
        };
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsLinux()) return $"linux-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return "unknown";
    }

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* best effort */ }
    }
}
