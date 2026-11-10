// x ticket — a tiny ticket system in SQLite, dstack-style: tickets stack up
// newest-first, each with a title, body text, a priority, tags, a queue
// (the category: dev, work, ...) and an additive comment thread. Listings
// are colorized by priority when stdout is a terminal.
//
// Storage: one `tickets` table in the shared x db (db_path from core config,
// default ~/.local/share/x/x.db). Same pipe as x note — no daemon, no setup.
//
// Applet overrides (~/.config/x/applets/ticket.toml):
//   max_results = 50    # cap for list/search output
//   color = true        # force ANSI colors even when piped

class TicketApplet : IApplet
{
    public string Name => "ticket";
    public string Blurb => "x's ticket system";
    public string Help => """
        Usage: x ticket                        list open tickets (newest first)
               x ticket all                    list all tickets, including done
               x ticket list [queue] [--all|--done] [-q <queue>] [-p <prio>] [-t <tag>] [--sort new|old|priority]
               x ticket add <title...> [-q <queue>] [-p <prio>] [-t <tag>]... [-d <text>]...
               x ticket show <id>              full ticket, including body
               x ticket edit <id> [--title <words...>] [-q <queue>] [-p <prio>] [-t <tag>]... [-d <text>]
               x ticket move <id> <queue>      reassign a ticket to another queue
               x ticket comment <id> <text...> append a comment (never replaces body)
               x ticket comments <id>          list a ticket's comments
               x ticket done <id...>           mark done (stamps closed)
               x ticket reopen <id...>         reopen
               x ticket rm <id...>             delete
               x ticket search <pattern...> [--all] [-q <queue>]
               x ticket tags                   tags on open tickets, with counts
               x ticket queues                 queues with open-ticket counts

        Priorities: low < normal < high < urgent (default normal). Tags are
        stored without '#'; matching is case-insensitive. Repeat -t for
        several tags, -d for several paragraphs (joined with newlines).
        In edit, -t replaces the whole tag set.

        Queues are categories (dev, work, ...): each ticket belongs to
        exactly one, "default" when not given. Filter with -q or a bare
        word: x ticket list dev. Matching is case-insensitive; queues print
        green as [dev] in listings.

        Comments are additive: x ticket comment 3 <text...> appends to the
        ticket's own thread, shown by `show` under the body. edit -d still
        replaces only the body. Comments are deleted with the ticket and
        are included in search.

        Listings are colorized by priority when stdout is a terminal
        (NO_COLOR or TERM=dumb disables): urgent bold red, high yellow,
        normal cyan, low gray. Tags print magenta as #tag, queues green as
        [dev]; done tickets print struck-through.

        Tickets live in the x SQLite database (db_path from core config).
        Empty listings exit 1.

        Applet overrides (~/.config/x/applets/ticket.toml):
          max_results = 50        # cap for list/search
          color = true            # force colors even when piped
        """;

    // Ordered low -> high; --sort priority ranks by the same order.
    private static readonly string[] Priorities = ["low", "normal", "high", "urgent"];

    private bool _color;

    public int Run(string[] args)
    {
        var cfg = Config.Load(Name);
        using var conn = Db.Open(cfg);

        Db.Exec(conn, """
            CREATE TABLE IF NOT EXISTS tickets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                body TEXT NOT NULL DEFAULT '',
                priority TEXT NOT NULL DEFAULT 'normal',
                tags TEXT NOT NULL DEFAULT '',
                queue TEXT NOT NULL DEFAULT 'default',
                status TEXT NOT NULL DEFAULT 'open',
                created TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
                closed TEXT
            );
            """);

        // Migrate databases created before queues existed: the CREATE above
        // is a no-op for them, so add the column explicitly.
        var hasQueue = false;
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(tickets)";
            using var r = pragma.ExecuteReader();
            while (r.Read())
                if (r.GetString(1) == "queue")
                    hasQueue = true;
        }
        if (!hasQueue)
            Db.Exec(conn, "ALTER TABLE tickets ADD COLUMN queue TEXT NOT NULL DEFAULT 'default'");

        Db.Exec(conn, """
            CREATE TABLE IF NOT EXISTS ticket_comments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ticket_id INTEGER NOT NULL,
                ts TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
                body TEXT NOT NULL
            );
            """);

        _color = UseColor(cfg);

        // No args: the stack — open tickets, newest first.
        if (args.Length == 0)
            return List(conn, cfg, [], forceAll: false);

        return args[0].ToLowerInvariant() switch
        {
            "list" => List(conn, cfg, args[1..], forceAll: false),
            "all" => List(conn, cfg, args[1..], forceAll: true),
            "add" => Add(conn, args[1..]),
            "show" => Show(conn, args[1..]),
            "edit" => Edit(conn, args[1..]),
            "done" => SetStatus(conn, "done", args[1..]),
            "reopen" => SetStatus(conn, "open", args[1..]),
            "rm" or "delete" => Delete(conn, args[1..]),
            "search" => Search(conn, cfg, args[1..]),
            "tags" => Tags(conn),
            "queues" => Queues(conn),
            "move" => Move(conn, args[1..]),
            "comment" or "c" => Comment(conn, args[1..]),
            "comments" => Comments(conn, args[1..]),
            "help" => PrintHelp(),
            _ => Usage($"unknown command '{args[0]}'"),
        };
    }

    // ---- commands ---------------------------------------------------------

    private int Add(Microsoft.Data.Sqlite.SqliteConnection conn, string[] args)
    {
        var draft = ParseTicketArgs(args, out var err);
        if (draft is null)
            return Usage(err);
        if (draft.Value.Title.Length == 0)
            return Usage("add needs a title");

        var priority = draft.Value.Priority ?? "normal";
        var queue = draft.Value.Queue ?? "default";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO tickets (title, body, priority, tags, queue) VALUES (@t, @b, @p, @g, @q)";
        cmd.Parameters.AddWithValue("@t", draft.Value.Title);
        cmd.Parameters.AddWithValue("@b", draft.Value.Body);
        cmd.Parameters.AddWithValue("@p", priority);
        cmd.Parameters.AddWithValue("@g", draft.Value.Tags);
        cmd.Parameters.AddWithValue("@q", queue);
        cmd.ExecuteNonQuery();

        // last_insert_rowid is per-connection, so this is the row just added.
        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        var id = (long)idCmd.ExecuteScalar()!;

        Console.WriteLine($"{Paint("90", $"#{id}")}  {Paint(PriorityColor(priority), priority.PadRight(6))}  {Paint("1", draft.Value.Title)}  {Paint("32", $"[{queue}]")}  {Paint("90", "created")}");
        return 0;
    }

    private int List(Microsoft.Data.Sqlite.SqliteConnection conn, Config cfg, string[] args, bool forceAll)
    {
        string? status = forceAll ? null : "open";   // null = all statuses
        string? priority = null;
        string? tag = null;
        string? queue = null;
        var sort = "new";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all": status = null; break;
                case "--done": status = "done"; break;
                case "--open": status = "open"; break;
                case "-p":
                case "--priority":
                    if (++i >= args.Length || NormalizePriority(args[i]) is not { } p)
                        return Usage("-p needs one of: low, normal, high, urgent");
                    priority = p;
                    break;
                case "-t":
                case "--tag":
                    if (++i >= args.Length)
                        return Usage("-t needs a tag");
                    tag = NormalizeTag(args[i]);
                    break;
                case "--sort":
                    if (++i >= args.Length || args[i] is not ("new" or "old" or "priority"))
                        return Usage("--sort needs new|old|priority");
                    sort = args[i];
                    break;
                case "-q":
                case "--queue":
                    if (++i >= args.Length || args[i].Trim().Length == 0)
                        return Usage("-q needs a queue name");
                    queue = args[i].Trim();
                    break;
                default:
                    if (args[i].StartsWith('-') && args[i] != "-")
                        return Usage($"unknown option '{args[i]}'");
                    // A bare word on list is a queue filter: x ticket list dev
                    if (queue is not null)
                        return Usage($"queue given twice ('{queue}' and '{args[i]}')");
                    queue = args[i];
                    break;
            }
        }

        var where = new List<string>();
        var bind = new List<(string Name, object Value)>();
        if (status is not null)
        {
            where.Add("status = @status");
            bind.Add(("@status", status));
        }
        if (priority is not null)
        {
            where.Add("priority = @prio");
            bind.Add(("@prio", priority));
        }
        if (tag is { Length: > 0 })
        {
            // Exact tag match: pad the stored list with commas so 'ops'
            // does not match 'devops'.
            where.Add("instr(',' || lower(tags) || ',', @tag) > 0");
            bind.Add(("@tag", $",{tag.ToLowerInvariant()},"));
        }
        if (queue is not null)
        {
            where.Add("lower(queue) = lower(@q)");
            bind.Add(("@q", queue));
        }

        var order = sort switch
        {
            "old" => "id ASC",
            "priority" => "CASE priority WHEN 'urgent' THEN 0 WHEN 'high' THEN 1 WHEN 'normal' THEN 2 ELSE 3 END ASC, id DESC",
            _ => "id DESC",   // stack order: newest first
        };

        var sql = "SELECT id, title, priority, queue, tags, status, created FROM tickets "
            + (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "")
            + $"ORDER BY {order} LIMIT @max";
        bind.Add(("@max", MaxResults(cfg)));

        var n = PrintRows(conn, sql, bind);
        if (n == 0)
        {
            Console.Error.WriteLine("x ticket: no matching tickets");
            return 1;
        }
        return 0;
    }

    private int Show(Microsoft.Data.Sqlite.SqliteConnection conn, string[] args)
    {
        if (args.Length != 1 || !long.TryParse(args[0], out var id))
            return Usage("show needs a ticket id");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, body, priority, tags, queue, status, created, closed FROM tickets WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            Console.Error.WriteLine($"x ticket: no ticket #{id}");
            return 1;
        }

        var title = reader.GetString(1);
        var body = reader.GetString(2);
        var priority = reader.GetString(3);
        var tags = reader.GetString(4);
        var queue = reader.GetString(5);
        var status = reader.GetString(6);
        var created = reader.GetString(7);
        var closed = reader.IsDBNull(8) ? null : reader.GetString(8);

        Console.WriteLine($"{Paint("90", $"#{id}")}  {Paint("1", title)}");
        var meta = $"priority: {Paint(PriorityColor(priority), priority)}   queue: {Paint("32", queue)}   status: {(status == "done" ? Paint("90", "done") : "open")}   created: {created}";
        if (closed is not null)
            meta += $"   closed: {closed}";
        Console.WriteLine(meta);
        if (tags.Length > 0)
            Console.WriteLine("tags: " + Paint("35", FormatTags(tags)));
        if (body.Length > 0)
            Console.WriteLine($"\n{body}");

        // The comment thread is additive history, printed after the body.
        using var ccmd = conn.CreateCommand();
        ccmd.CommandText = "SELECT ts, body FROM ticket_comments WHERE ticket_id = @id ORDER BY id";
        ccmd.Parameters.AddWithValue("@id", id);
        using var cr = ccmd.ExecuteReader();
        var anyComment = false;
        while (cr.Read())
        {
            if (!anyComment)
            {
                Console.WriteLine("comments:");
                anyComment = true;
            }
            Console.WriteLine($"  {Paint("90", cr.GetString(0))}  {cr.GetString(1)}");
        }
        return 0;
    }

    private int Edit(Microsoft.Data.Sqlite.SqliteConnection conn, string[] args)
    {
        if (args.Length == 0 || !long.TryParse(args[0], out var id))
            return Usage("edit needs a ticket id");

        var draft = ParseTicketArgs(args[1..], out var err);
        if (draft is null)
            return Usage(err);
        var (title, priority, queue, tags, body) = draft.Value;
        if (title.Length == 0 && priority is null && queue is null && tags.Length == 0 && body.Length == 0)
            return Usage("edit needs at least one change: --title words, -q, -p, -t or -d");

        // Only the fields actually given are updated; -t replaces the whole
        // tag set rather than appending.
        var sets = new List<string>();
        var bind = new List<(string Name, object Value)>();
        if (title.Length > 0) { sets.Add("title = @t"); bind.Add(("@t", title)); }
        if (queue is not null) { sets.Add("queue = @q"); bind.Add(("@q", queue)); }
        if (priority is not null) { sets.Add("priority = @p"); bind.Add(("@p", priority)); }
        if (tags.Length > 0) { sets.Add("tags = @g"); bind.Add(("@g", tags)); }
        if (body.Length > 0) { sets.Add("body = @b"); bind.Add(("@b", body)); }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE tickets SET {string.Join(", ", sets)} WHERE id = @id";
        foreach (var (name, value) in bind)
            cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.AddWithValue("@id", id);
        if (cmd.ExecuteNonQuery() == 0)
        {
            Console.Error.WriteLine($"x ticket: no ticket #{id}");
            return 1;
        }

        Console.WriteLine($"{Paint("90", $"#{id}")}  updated");
        return 0;
    }

    private int SetStatus(Microsoft.Data.Sqlite.SqliteConnection conn, string status, string[] args)
    {
        if (args.Length == 0)
            return Usage($"{status} needs at least one ticket id");

        var rc = 0;
        foreach (var a in args)
        {
            if (!long.TryParse(a, out var id))
            {
                Console.Error.WriteLine($"x ticket: '{a}' is not a ticket id");
                rc = 1;
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE tickets
                SET status = @s,
                    closed = CASE WHEN @s = 'done' THEN datetime('now', 'localtime') ELSE NULL END
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@s", status);
            cmd.Parameters.AddWithValue("@id", id);
            if (cmd.ExecuteNonQuery() == 0)
            {
                Console.Error.WriteLine($"x ticket: no ticket #{id}");
                rc = 1;
                continue;
            }
            Console.WriteLine($"{Paint("90", $"#{id}")}  {(status == "done" ? "done" : "reopened")}");
        }
        return rc;
    }

    private int Delete(Microsoft.Data.Sqlite.SqliteConnection conn, string[] args)
    {
        if (args.Length == 0)
            return Usage("rm needs at least one ticket id");

        var rc = 0;
        foreach (var a in args)
        {
            if (!long.TryParse(a, out var id))
            {
                Console.Error.WriteLine($"x ticket: '{a}' is not a ticket id");
                rc = 1;
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tickets WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            if (cmd.ExecuteNonQuery() == 0)
            {
                Console.Error.WriteLine($"x ticket: no ticket #{id}");
                rc = 1;
                continue;
            }
            // Comments die with their ticket.
            using var ccmd = conn.CreateCommand();
            ccmd.CommandText = "DELETE FROM ticket_comments WHERE ticket_id = @id";
            ccmd.Parameters.AddWithValue("@id", id);
            ccmd.ExecuteNonQuery();
            Console.WriteLine($"{Paint("90", $"#{id}")}  removed");
        }
        return rc;
    }

    private int Move(Microsoft.Data.Sqlite.SqliteConnection conn, string[] args)
    {
        if (args.Length != 2 || !long.TryParse(args[0], out var id))
            return Usage("move needs a ticket id and a queue: x ticket move <id> <queue>");
        var queue = args[1].Trim();
        if (queue.Length == 0)
            return Usage("move needs a queue name");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tickets SET queue = @q WHERE id = @id";
        cmd.Parameters.AddWithValue("@q", queue);
        cmd.Parameters.AddWithValue("@id", id);
        if (cmd.ExecuteNonQuery() == 0)
        {
            Console.Error.WriteLine($"x ticket: no ticket #{id}");
            return 1;
        }
        Console.WriteLine($"{Paint("90", $"#{id}")}  {Paint("32", $"[{queue}]")}");
        return 0;
    }

    private int Queues(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT queue, COUNT(*) FROM tickets
            WHERE status = 'open'
            GROUP BY queue ORDER BY COUNT(*) DESC, lower(queue)
            """;
        using var reader = cmd.ExecuteReader();
        var any = false;
        while (reader.Read())
        {
            any = true;
            Console.WriteLine($"{Paint("32", $"[{reader.GetString(0)}]")}  {Paint("90", $"({reader.GetInt64(1)})")}");
        }
        if (!any)
        {
            Console.Error.WriteLine("x ticket: no open tickets");
            return 1;
        }
        return 0;
    }

    private int Comment(Microsoft.Data.Sqlite.SqliteConnection conn, string[] args)
    {
        if (args.Length < 2 || !long.TryParse(args[0], out var id))
            return Usage("comment needs a ticket id and text: x ticket comment <id> <text...>");

        // Check the ticket exists first so a typo'd id is not mistaken for
        // an empty comment.
        if (!TicketExists(conn, id))
        {
            Console.Error.WriteLine($"x ticket: no ticket #{id}");
            return 1;
        }

        var text = string.Join(' ', args[1..]);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ticket_comments (ticket_id, body) VALUES (@id, @b)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@b", text);
        cmd.ExecuteNonQuery();
        Console.WriteLine($"{Paint("90", $"#{id}")}  commented");
        return 0;
    }

    private int Comments(Microsoft.Data.Sqlite.SqliteConnection conn, string[] args)
    {
        if (args.Length != 1 || !long.TryParse(args[0], out var id))
            return Usage("comments needs a ticket id");
        if (!TicketExists(conn, id))
        {
            Console.Error.WriteLine($"x ticket: no ticket #{id}");
            return 1;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ts, body FROM ticket_comments WHERE ticket_id = @id ORDER BY id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        var any = false;
        while (reader.Read())
        {
            any = true;
            Console.WriteLine($"{Paint("90", reader.GetString(0))}  {reader.GetString(1)}");
        }
        if (!any)
        {
            Console.Error.WriteLine($"x ticket: no comments on #{id}");
            return 1;
        }
        return 0;
    }

    private static bool TicketExists(Microsoft.Data.Sqlite.SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tickets WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private int Search(Microsoft.Data.Sqlite.SqliteConnection conn, Config cfg, string[] args)
    {
        var all = false;
        string? queue = null;
        var words = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all": all = true; break;
                case "-q":
                case "--queue":
                    if (++i >= args.Length || args[i].Trim().Length == 0)
                        return Usage("-q needs a queue name");
                    queue = args[i].Trim();
                    break;
                default:
                    if (args[i].StartsWith('-') && args[i] != "-") return Usage($"unknown option '{args[i]}'");
                    words.Add(args[i]);
                    break;
            }
        }
        if (words.Count == 0)
            return Usage("search needs a pattern");

        // instr, not LIKE, so % and _ in the pattern stay literal.
        var pattern = string.Join(' ', words).ToLowerInvariant();
        var sql = "SELECT id, title, priority, queue, tags, status, created FROM tickets WHERE "
            + (all ? "" : "status = 'open' AND ")
            + (queue is not null ? "lower(queue) = lower(@q) AND " : "")
            + "(instr(lower(title || ' ' || body), @p) > 0 OR EXISTS (SELECT 1 FROM ticket_comments c WHERE c.ticket_id = tickets.id AND instr(lower(c.body), @p) > 0)) ORDER BY id DESC LIMIT @max";
        var bind = new List<(string Name, object Value)>
        {
            ("@p", pattern),
            ("@max", MaxResults(cfg)),
        };
        if (queue is not null)
            bind.Add(("@q", queue));

        var n = PrintRows(conn, sql, bind);
        if (n == 0)
        {
            Console.Error.WriteLine("x ticket: no matching tickets");
            return 1;
        }
        return 0;
    }

    private int Tags(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tags FROM tickets WHERE status = 'open'";
        using var reader = cmd.ExecuteReader();

        // Count per tag (case-insensitive key, first-seen spelling shown).
        var counts = new Dictionary<string, (string? Display, int Count)>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            foreach (var t in reader.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = t.Trim();
                if (key.Length == 0)
                    continue;
                counts.TryGetValue(key, out var cur);
                counts[key] = (cur.Display is { Length: > 0 } ? cur.Display : key, cur.Count + 1);
            }
        }

        if (counts.Count == 0)
        {
            Console.Error.WriteLine("x ticket: no tags on open tickets");
            return 1;
        }
        foreach (var (display, count) in counts.Values
                     .OrderByDescending(c => c.Count)
                     .ThenBy(c => c.Display, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{Paint("35", "#" + display)}  {Paint("90", $"({count})")}");
        return 0;
    }

    // ---- argument + output helpers ---------------------------------------

    /// Parses add/edit arguments: free words become the title; -q, -p, -t
    /// and -d are options (-t and -d repeat). Priority and queue stay null
    /// when not given so edit can tell "not provided" apart from defaults.
    private static (string Title, string? Priority, string? Queue, string Tags, string Body)? ParseTicketArgs(
        string[] args, out string error)
    {
        var words = new List<string>();
        var tags = new List<string>();
        var bodyParts = new List<string>();
        string? priority = null;
        string? queue = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p":
                case "--priority":
                    if (++i >= args.Length || NormalizePriority(args[i]) is not { } p)
                    {
                        error = "-p needs one of: low, normal, high, urgent";
                        return null;
                    }
                    priority = p;
                    break;
                case "-t":
                case "--tag":
                    if (++i >= args.Length)
                    {
                        error = "-t needs a tag";
                        return null;
                    }
                    var tag = NormalizeTag(args[i]);
                    if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        tags.Add(tag);
                    break;
                case "--title":
                    // Explicit marker only — the free words that follow are
                    // the title either way.
                    break;
                case "-q":
                case "--queue":
                    if (++i >= args.Length || args[i].Trim().Length == 0)
                    {
                        error = "-q needs a queue name";
                        return null;
                    }
                    queue = args[i].Trim();
                    break;
                case "-d":
                case "--text":
                case "--body":
                case "--desc":
                    if (++i >= args.Length)
                    {
                        error = "-d needs text";
                        return null;
                    }
                    bodyParts.Add(args[i]);
                    break;
                default:
                    if (args[i].StartsWith('-') && args[i] != "-")
                    {
                        error = $"unknown option '{args[i]}'";
                        return null;
                    }
                    words.Add(args[i]);
                    break;
            }
        }

        error = "";
        return (string.Join(' ', words), priority, queue, string.Join(',', tags), string.Join('\n', bodyParts));
    }

    /// Prints one line per ticket: #id, colorized priority, bold title,
    /// magenta tags, date. Done tickets print struck-through. Returns the
    /// number of rows printed.
    private int PrintRows(Microsoft.Data.Sqlite.SqliteConnection conn, string sql,
        List<(string Name, object Value)> bind)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in bind)
            cmd.Parameters.AddWithValue(name, value);
        using var reader = cmd.ExecuteReader();

        var n = 0;
        while (reader.Read())
        {
            n++;
            var id = reader.GetInt64(0);
            var title = reader.GetString(1);
            var priority = reader.GetString(2);
            var queue = reader.GetString(3);
            var tags = reader.GetString(4);
            var status = reader.GetString(5);
            var created = reader.GetString(6);

            var done = status == "done";
            var line = $"{Paint("90", $"#{id}")}  {Paint(PriorityColor(priority), priority.PadRight(6))}  "
                + (done ? Paint("9;90", title) : Paint("1", title))
                + "  " + Paint("32", $"[{queue}]");
            if (tags.Length > 0)
                line += "  " + Paint("35", FormatTags(tags));
            if (done)
                line += "  " + Paint("90", "(done)");
            line += "  " + Paint("90", created[..10]);
            Console.WriteLine(line);
        }
        return n;
    }

    private static string? NormalizePriority(string s)
    {
        var p = s.ToLowerInvariant();
        return Priorities.Contains(p) ? p : null;
    }

    /// Tags are stored without a leading '#' so `#bug` and `bug` are the
    /// same tag; case-insensitive matching happens in SQL with lower().
    private static string NormalizeTag(string tag) => tag.Trim().TrimStart('#').Trim();

    private static string FormatTags(string tags) =>
        string.Join(" ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => "#" + t));

    private static string PriorityColor(string p) => p switch
    {
        "urgent" => "1;31",
        "high" => "33",
        "normal" => "36",
        _ => "90",
    };

    /// Explicit `color` in the applet overlay wins; otherwise colors only on
    /// a real terminal, honoring NO_COLOR and TERM=dumb.
    private static bool UseColor(Config cfg)
    {
        if (cfg.Raw.TryGetValue("color", out var c) && bool.TryParse(c, out var b))
            return b;
        return !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null
            && Environment.GetEnvironmentVariable("TERM") != "dumb";
    }

    private string Paint(string code, string text) =>
        _color ? $"\u001b[{code}m{text}\u001b[0m" : text;

    private static int MaxResults(Config cfg)
    {
        if (cfg.Raw.TryGetValue("max_results", out var m) && int.TryParse(m, out var n) && n > 0)
            return n;
        return 50;
    }

    private int Usage(string message)
    {
        Console.Error.WriteLine($"x ticket: {message}");
        Console.Error.WriteLine("Run 'x help ticket' for usage.");
        return 1;
    }

    private int PrintHelp()
    {
        Console.WriteLine(Help);
        return 0;
    }
}
