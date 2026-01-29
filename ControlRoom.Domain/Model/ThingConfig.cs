using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlRoom.Domain.Model;

/// <summary>
/// Configuration for a Thing, stored as JSON in Thing.ConfigJson.
/// Supports multiple run profiles with args/env presets.
/// </summary>
public sealed class ThingConfig
{
    public const int CurrentSchema = 2;

    /// <summary>
    /// Schema version for migration
    /// </summary>
    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    /// <summary>
    /// Path to the script file
    /// </summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>
    /// Default working directory (null = script's directory)
    /// </summary>
    [JsonPropertyName("workingDir")]
    public string? WorkingDir { get; init; }

    /// <summary>
    /// Run profiles (presets for args, env, etc.)
    /// </summary>
    [JsonPropertyName("profiles")]
    public List<ThingProfile> Profiles { get; init; } = [];

    /// <summary>
    /// Get the default profile (first one, or a generated default)
    /// </summary>
    public ThingProfile GetDefaultProfile()
    {
        return Profiles.FirstOrDefault() ?? ThingProfile.Default;
    }

    /// <summary>
    /// Get a profile by ID
    /// </summary>
    public ThingProfile? GetProfile(string profileId)
    {
        return Profiles.FirstOrDefault(p => p.Id == profileId);
    }

    /// <summary>
    /// Parse from JSON string, migrating old schema if needed
    /// </summary>
    public static ThingConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check schema version
        var schema = root.TryGetProperty("schema", out var schemaProp) ? schemaProp.GetInt32() : 1;

        if (schema >= 2)
        {
            // Current schema - deserialize directly
            return JsonSerializer.Deserialize<ThingConfig>(json)!;
        }

        // Schema 1 (legacy): migrate to schema 2
        var path = root.GetProperty("path").GetString()!;
        var workingDir = root.TryGetProperty("workingDir", out var wdProp) ? wdProp.GetString() : null;

        return new ThingConfig
        {
            Schema = CurrentSchema,
            Path = path,
            WorkingDir = workingDir,
            Profiles = [ThingProfile.Default]
        };
    }

    /// <summary>
    /// Serialize to JSON
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
}

/// <summary>
/// A run profile with preset args and environment variables
/// </summary>
public sealed class ThingProfile
{
    /// <summary>
    /// Unique identifier for this profile
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Display name for the profile
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Command line arguments
    /// </summary>
    [JsonPropertyName("args")]
    public string Args { get; init; } = "";

    /// <summary>
    /// Environment variable overrides
    /// </summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; init; } = [];

    /// <summary>
    /// Working directory override (null = use Thing's default)
    /// </summary>
    [JsonPropertyName("workingDir")]
    public string? WorkingDir { get; init; }

    /// <summary>
    /// Default profile (used when no profiles are defined)
    /// </summary>
    public static ThingProfile Default => new()
    {
        Id = "default",
        Name = "Default",
        Args = "",
        Env = []
    };
}
