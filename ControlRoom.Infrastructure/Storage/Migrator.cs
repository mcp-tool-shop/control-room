namespace ControlRoom.Infrastructure.Storage;

public sealed class Migrator
{
    private readonly Db _db;

    public Migrator(Db db) => _db = db;

    public void EnsureCreated(string schemaSql)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = schemaSql;
        cmd.ExecuteNonQuery();
    }
}
