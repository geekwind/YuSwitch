using System.ComponentModel.DataAnnotations;

namespace YuSwitch.Data.Entities;

/// <summary>
/// A model served by a service. A model name (e.g. "gpt-4o") can map to
/// multiple services for load balancing / failover.
///
/// Model name mapping: <see cref="ModelName"/> is the name exposed to clients
/// (the unified alias). <see cref="UpstreamModel"/> is the actual model name
/// sent to this service's upstream. When UpstreamModel is empty, ModelName is
/// used as-is. This lets one logical model resolve to different real names
/// across providers — e.g. client requests "model-alias", service A serves it as
/// "model-name-A", service B serves it as "model-name-B".
/// </summary>
public class ModelEntity
{
    public int Id { get; set; }

    public int ServiceId { get; set; }
    public ServiceEntity Service { get; set; } = null!;

    /// <summary>Model name as exposed to clients (unified alias, e.g. "model-alias").</summary>
    [Required, MaxLength(128)]
    public string ModelName { get; set; } = "";

    /// <summary>Actual model name to send to this service's upstream.
    /// Empty = use ModelName as-is. This is the per-service name mapping.</summary>
    [MaxLength(128)]
    public string UpstreamModel { get; set; } = "";

    /// <summary>Comma-separated additional aliases that should also resolve to
    /// this row (e.g. "model-name-A,model-name-B"). Lets a service register
    /// its real model under several client-facing names without duplicate rows.</summary>
    [MaxLength(512)]
    public string Aliases { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public bool SupportsVision { get; set; } = false;
    public bool SupportsTools { get; set; } = true;
    public bool SupportsReasoning { get; set; } = false;
    public bool SupportsEmbeddings { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>True if this row serves the requested model name (exact or alias match).</summary>
    public bool Matches(string requestedModel) =>
        string.Equals(ModelName, requestedModel, StringComparison.OrdinalIgnoreCase) ||
        (Aliases?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Contains(requestedModel, StringComparer.OrdinalIgnoreCase) ?? false);

    /// <summary>The model name to send upstream: UpstreamModel if set, else ModelName.</summary>
    public string ResolveUpstreamModel() =>
        string.IsNullOrWhiteSpace(UpstreamModel) ? ModelName : UpstreamModel;
}
