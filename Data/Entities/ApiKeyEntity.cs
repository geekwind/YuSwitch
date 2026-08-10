using System.ComponentModel.DataAnnotations;

namespace YuSwitch.Data.Entities;

/// <summary>
/// An API key issued by the gateway to downstream clients. Replaces the
/// legacy single-key / multi-key config. Supports per-key model allowlist.
/// </summary>
public class ApiKeyEntity
{
    public int Id { get; set; }

    /// <summary>The actual key string (without "sk-" prefix stored separately if desired).</summary>
    [Required, MaxLength(256)]
    public string KeyValue { get; set; } = "";

    /// <summary>Display name.</summary>
    [MaxLength(128)]
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Comma-separated allowed model names, or "*" for all.</summary>
    [MaxLength(2048)]
    public string AllowedModels { get; set; } = "*";

    /// <summary>Per-key QPM limit (0 = no limit).</summary>
    public int QpmLimit { get; set; } = 0;

    /// <summary>Per-key daily request quota (0 = unlimited).</summary>
    public int DailyQuota { get; set; } = 0;

    /// <summary>Optional IP allowlist (CIDR, comma-separated).</summary>
    [MaxLength(1024)]
    public string IpAllowlist { get; set; } = "";

    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>True if allowedModels is "*" or contains model.</summary>
    public bool AllowsModel(string model) =>
        AllowedModels == "*" ||
        AllowedModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Contains(model, StringComparer.OrdinalIgnoreCase);
}
