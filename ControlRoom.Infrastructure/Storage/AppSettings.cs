using System.Text.Json;

namespace ControlRoom.Infrastructure.Storage;

/// <summary>
/// Simple key-value settings store backed by SQLite.
/// All settings are serialized as JSON values for flexibility.
/// Optimistic write: skips DB write if value unchanged.
/// </summary>
public sealed class AppSettings
{
    private readonly Db _db;

    public AppSettings(Db db)
    {
        _db = db;
    }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);

        var result = cmd.ExecuteScalar();
        if (result is null or DBNull)
            return defaultValue;

        var json = (string)result;
        return JsonSerializer.Deserialize<T>(json);
    }

    public void Set<T>(string key, T value)
    {
        var newJson = JsonSerializer.Serialize(value);

        using var conn = _db.Open();

        // Check existing value first (optimistic write)
        using (var readCmd = conn.CreateCommand())
        {
            readCmd.CommandText = "SELECT value_json FROM settings WHERE key = $key";
            readCmd.Parameters.AddWithValue("$key", key);

            var existing = readCmd.ExecuteScalar();
            if (existing is string existingJson && existingJson == newJson)
            {
                // Value unchanged, skip write
                return;
            }
        }

        // Value changed or new, write it
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value_json, updated_at)
            VALUES ($key, $value, $at)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", newJson);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Remove(string key)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Well-known setting keys
    /// </summary>
    public static class Keys
    {
        public const string WindowState = "window.state";
        public const string LastRoute = "navigation.lastRoute";
        public const string PaletteLastQuery = "palette.lastQuery";
    }

    /// <summary>
    /// Stable route identifiers (decoupled from actual route strings)
    /// </summary>
    public static class Routes
    {
        public const string Timeline = "timeline";
        public const string Things = "things";
        public const string Failures = "failures";

        public static string? ToRouteString(string? stableId) => stableId switch
        {
            Timeline => "//timeline",
            Things => "//things",
            Failures => "//failures",
            _ => null
        };

        public static string? ToStableId(string? routeString)
        {
            if (string.IsNullOrEmpty(routeString)) return null;

            if (routeString.StartsWith("//timeline")) return Timeline;
            if (routeString.StartsWith("//things")) return Things;
            if (routeString.StartsWith("//failures")) return Failures;

            return null;
        }
    }
}

/// <summary>
/// Window state for persistence
/// </summary>
public sealed record WindowState(
    double X,
    double Y,
    double Width,
    double Height,
    bool IsMaximized
);
