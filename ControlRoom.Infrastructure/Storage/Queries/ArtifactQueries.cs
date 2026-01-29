using Microsoft.Data.Sqlite;
using ControlRoom.Domain.Model;

namespace ControlRoom.Infrastructure.Storage.Queries;

public sealed record ArtifactListItem(
    ArtifactId ArtifactId,
    RunId RunId,
    string MediaType,
    string Locator,
    string? Sha256Hex,
    DateTimeOffset CreatedAt,
    string FileName,
    long? FileSizeBytes
);

public sealed class ArtifactQueries
{
    private readonly Db _db;

    public ArtifactQueries(Db db) => _db = db;

    public IReadOnlyList<ArtifactListItem> ListArtifactsForRun(RunId runId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT artifact_id, run_id, media_type, locator, sha256_hex, created_at
            FROM artifacts
            WHERE run_id = $run_id
            ORDER BY created_at ASC
            """;
        cmd.Parameters.AddWithValue("$run_id", runId.ToString());

        var list = new List<ArtifactListItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var locator = reader.GetString(3);
            var fileName = Path.GetFileName(locator);
            long? fileSize = null;

            // Try to get file size if it exists
            if (File.Exists(locator))
            {
                try { fileSize = new FileInfo(locator).Length; }
                catch { /* ignore */ }
            }

            list.Add(new ArtifactListItem(
                new ArtifactId(Guid.Parse(reader.GetString(0))),
                new RunId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                locator,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                fileName,
                fileSize
            ));
        }
        return list;
    }

    public ArtifactListItem? GetArtifact(ArtifactId artifactId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT artifact_id, run_id, media_type, locator, sha256_hex, created_at
            FROM artifacts
            WHERE artifact_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", artifactId.ToString());

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var locator = reader.GetString(3);
        var fileName = Path.GetFileName(locator);
        long? fileSize = null;

        if (File.Exists(locator))
        {
            try { fileSize = new FileInfo(locator).Length; }
            catch { /* ignore */ }
        }

        return new ArtifactListItem(
            new ArtifactId(Guid.Parse(reader.GetString(0))),
            new RunId(Guid.Parse(reader.GetString(1))),
            reader.GetString(2),
            locator,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            fileName,
            fileSize
        );
    }
}
