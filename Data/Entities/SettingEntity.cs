using System.ComponentModel.DataAnnotations;

namespace YuSwitch.Data.Entities;

/// <summary>
/// Generic key/value application setting (software name, subtitle, etc.).
/// Editable from the settings page; cached in memory for the UI shell.
/// </summary>
public class SettingEntity
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Key { get; set; } = "";

    [MaxLength(512)]
    public string Value { get; set; } = "";
}
