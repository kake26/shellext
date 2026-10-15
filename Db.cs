// Shared data layer — SQLite, one file, AOT-safe.

/// One connection per applet invocation. No pooling needed at this scale.
static class Db
{
    /// Opens (creating if needed) the shared database and ensures the
    /// schema for a given table exists. Applets declare their own tables.
    public static Microsoft.Data.Sqlite.SqliteConnection Open(Config cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.DbPath)!);
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={cfg.DbPath}");
        conn.Open();
        return conn;
    }

    public static void Exec(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
