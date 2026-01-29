using Microsoft.Data.Sqlite;
using ControlRoom.Domain.Model;

namespace ControlRoom.Infrastructure.Storage.Queries;

public sealed record ThingListItem(
    ThingId ThingId,
    string Name,
    ThingKind Kind,
    string ConfigJson
);

public sealed class ThingQueries
{
    private readonly Db _db;
    public ThingQueries(Db db) => _db = db;

    public IReadOnlyList<ThingListItem> ListThings()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT thing_id, name, kind, config_json
            FROM things
            ORDER BY created_at DESC
            """;

        var list = new List<ThingListItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ThingListItem(
                new ThingId(Guid.Parse(r.GetString(0))),
                r.GetString(1),
                (ThingKind)r.GetInt32(2),
                r.GetString(3)
            ));
        }
        return list;
    }

    public ThingListItem? GetThing(ThingId thingId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT thing_id, name, kind, config_json
            FROM things
            WHERE thing_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", thingId.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return new ThingListItem(
            new ThingId(Guid.Parse(r.GetString(0))),
            r.GetString(1),
            (ThingKind)r.GetInt32(2),
            r.GetString(3)
        );
    }

    public void InsertThing(Thing thing)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO things(thing_id, name, kind, config_json, created_at)
            VALUES ($id, $name, $kind, $cfg, $at)
            """;
        cmd.Parameters.AddWithValue("$id", thing.Id.ToString());
        cmd.Parameters.AddWithValue("$name", thing.Name);
        cmd.Parameters.AddWithValue("$kind", (int)thing.Kind);
        cmd.Parameters.AddWithValue("$cfg", thing.ConfigJson);
        cmd.Parameters.AddWithValue("$at", thing.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
