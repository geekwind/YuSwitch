using Microsoft.EntityFrameworkCore;
using YuSwitch.Data;
using YuSwitch.Data.Entities;

namespace YuSwitch.Services;

/// <summary>
/// Configuration/data access service. Reads service+model+apikey from the
/// database, with an in-memory cache refreshed on writes so the hot request
/// path doesn't hit SQLite on every call.
/// </summary>
public class ConfigService
{
    private readonly IDbContextFactory<AppDbContext> _dbf;
    private volatile ConfigSnapshot _snapshot;

    public ConfigService(IDbContextFactory<AppDbContext> dbf)
    {
        _dbf = dbf;
        _snapshot = new ConfigSnapshot([], [], []);
    }

    /// <summary>Current in-memory snapshot of all config.</summary>
    public ConfigSnapshot Snapshot => _snapshot;

    /// <summary>Reload the snapshot from the database.</summary>
    public async Task<ConfigSnapshot> ReloadAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var services = await db.Services.AsNoTracking().ToListAsync(ct);
        var models = await db.Models.AsNoTracking().ToListAsync(ct);
        var keys = await db.ApiKeys.AsNoTracking().ToListAsync(ct);
        var snap = new ConfigSnapshot(services, models, keys);
        _snapshot = snap;
        return snap;
    }

    public ApiKeyEntity? FindApiKey(string keyValue) =>
        _snapshot.ApiKeys.FirstOrDefault(k => k.Enabled && k.KeyValue == keyValue);

    /// <summary>
    /// Find all enabled services + their models that serve the requested model
    /// name. Matching is by exact ModelName OR by the Aliases list on each
    /// ModelEntity, so a client can request "model-alias" and hit services that
    /// registered it under "model-name-A" by listing it as an alias.
    /// Ordered by priority (failover) then weight desc (load balance tier).
    /// </summary>
    public List<(ServiceEntity Service, ModelEntity Model)> FindServicesForModel(string modelName)
    {
        var snap = _snapshot;
        var result = new List<(ServiceEntity, ModelEntity)>();
        foreach (var m in snap.Models)
        {
            if (!m.Enabled) continue;
            if (!m.Matches(modelName)) continue;
            var svc = snap.Services.FirstOrDefault(s => s.Id == m.ServiceId && s.Enabled);
            if (svc is null) continue;
            result.Add((svc, m));
        }

        return result
            .OrderBy(c => c.Item1.Priority)
            .ThenByDescending(c => c.Item1.Weight)
            .ToList();
    }

    /// <summary>All client-facing model names (for /v1/models). Includes
    /// ModelName plus all aliases so clients see everything they can request.</summary>
    public List<string> GetEnabledModelNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _snapshot.Models.Where(m => m.Enabled))
        {
            names.Add(m.ModelName);
            if (!string.IsNullOrWhiteSpace(m.Aliases))
                foreach (var a in m.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    names.Add(a);
        }
        return names.OrderBy(n => n).ToList();
    }
}

public record ConfigSnapshot(
    List<ServiceEntity> Services,
    List<ModelEntity> Models,
    List<ApiKeyEntity> ApiKeys);
