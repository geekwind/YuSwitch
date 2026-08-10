namespace YuSwitch.Services;

/// <summary>
/// Version of the running binary. Backed by the assembly version, which the
/// csproj &lt;Version&gt; sets for local builds and the release pipeline
/// overrides with the git tag (dotnet publish -p:Version=&lt;tag&gt;).
/// </summary>
public class AppVersionService
{
    /// <summary>Semantic version, e.g. "0.1.0" (revision dropped).</summary>
    public string Version { get; }

    public AppVersionService()
    {
        var v = typeof(AppVersionService).Assembly.GetName().Version;
        Version = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
