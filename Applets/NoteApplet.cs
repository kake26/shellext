// x note — quick notes in SQLite. The reference applet: one verb, standalone,
// zero-config, proves the whole pipe (config -> db -> output).

class NoteApplet : IApplet
{
    public string Name => "note";
    public string Blurb => "quick notes (append/read)";
    public string Help => """
        Usage: x note <text>     append a timestamped note
               x note            show the last max_results notes (default 20)
               x note search <pattern>   filter notes (case-insensitive)

        Notes live in the x SQLite database (db_path from core config,
        default ~/.local/share/x/x.db). No daemon, no setup.

        Applet overrides (~/.config/x/applets/note.toml):
          # db_path = "/custom/path.db"
          max_results = 20        # cap for the default listing
        """;

    public int Run(string[] args)
    {
        var cfg = Config.Load(Name);
        using var conn = Db.Open(cfg);

        Db.Exec(conn, """
            CREATE TABLE IF NOT EXISTS notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
                body TEXT NOT NULL
            );
            """);

        // max_results caps the default listing; anything unparseable or
        // non-positive falls back to the default.
        var max = 20;
        if (cfg.Raw.TryGetValue("max_results", out var m) && int.TryParse(m, out var n) && n > 0)
            max = n;

        // No args: read recent notes.
        if (args.Length == 0)
            return Print(conn, $"ORDER BY id DESC LIMIT {max}");

        // x note search <pattern>
        if (args[0] == "search" && args.Length >= 2)
        {
            var pattern = string.Join(' ', args[1..]).ToLowerInvariant();
            return Print(conn, "WHERE instr(lower(body), @p) > 0 ORDER BY id DESC LIMIT 200",
                ("@p", pattern));
        }

        // Otherwise: append a note.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO notes (body) VALUES (@b)";
        cmd.Parameters.AddWithValue("@b", string.Join(' ', args));
        cmd.ExecuteNonQuery();
        return 0;
    }

    private static int Print(Microsoft.Data.Sqlite.SqliteConnection conn, string where,
        params (string Name, string Value)[] bind)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT ts, body FROM notes {where}";
        foreach (var (name, value) in bind)
            cmd.Parameters.AddWithValue(name, value);
        using var reader = cmd.ExecuteReader();
        var any = false;
        while (reader.Read())
        {
            any = true;
            Console.WriteLine($"[{reader.GetString(0)}] {reader.GetString(1)}");
        }
        return any ? 0 : 1;
    }
}
