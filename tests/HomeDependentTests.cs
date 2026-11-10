// Applet/config behaviors that depend on ~/.config/x — these tests point
// HOME at a temp directory and drive the real applets. All HOME-dependent
// tests live in this one class so xunit runs them sequentially and the
// shared home is never mutated concurrently.

using Xunit;

public class HomeDependentTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(),
        $"x-home-{Guid.NewGuid():N}");
    private readonly string _oldHome = Environment.GetEnvironmentVariable("HOME") ?? "";

    public HomeDependentTests() => Directory.CreateDirectory(_home);
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _oldHome);
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    private void SetHome() => Environment.SetEnvironmentVariable("HOME", _home);

    private void WriteCoreToml(string toml)
    {
        Directory.CreateDirectory(Path.Combine(_home, ".config", "x"));
        File.WriteAllText(Path.Combine(_home, ".config", "x", "x.toml"), toml);
    }

    private void WriteAppletToml(string applet, string toml)
    {
        Directory.CreateDirectory(Path.Combine(_home, ".config", "x", "applets"));
        File.WriteAllText(
            Path.Combine(_home, ".config", "x", "applets", $"{applet}.toml"), toml);
    }

    // --- Config paths -----------------------------------------------------

    [Fact]
    public void DbPathExpandsTilde()
    {
        SetHome();
        WriteCoreToml("db_path = \"~/x.db\"\n");
        Assert.Equal(Path.Combine(_home, "x.db"), Config.Load("note").DbPath);
    }

    [Fact]
    public void DataDirExpandsTildeAndDefaultsDbIntoIt()
    {
        SetHome();
        WriteCoreToml("data_dir = \"~/datad\"\n");
        Assert.Equal(Path.Combine(_home, "datad", "x.db"), Config.Load("note").DbPath);
    }

    [Fact]
    public void BareDbPathStaysRelative()
    {
        SetHome();
        WriteCoreToml("db_path = \"x.db\"\n");
        Assert.Equal("x.db", Config.Load("note").DbPath);
    }

    // --- note -------------------------------------------------------------

    private (int Exit, string Stdout, string Stderr) RunNote(params string[] args)
    {
        SetHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var rc = new NoteApplet().Run(args);
            return (rc, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void NoteSearchWithoutPatternIsAUsageError()
    {
        var (exit, _, stderr) = RunNote("search");
        Assert.Equal(1, exit);
        Assert.Contains("needs a pattern", stderr);
    }

    [Fact]
    public void NoteMaxResultsCapsTheDefaultListing()
    {
        SetHome();
        WriteAppletToml("note", "max_results = 2\n");

        foreach (var body in new[] { "one", "two", "three" })
            Assert.Equal(0, new NoteApplet().Run([body]));

        var (exit, stdout, _) = RunNote();
        Assert.Equal(0, exit);
        Assert.Equal(2, stdout.Lines().Count(l => l.Length > 0));
    }

    // --- rc ---------------------------------------------------------------

    private string RunRc(params string[] args)
    {
        SetHome();
        var stdout = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(stdout);
        try { Assert.Equal(0, new RcApplet().Run(args)); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        return stdout.ToString();
    }

    [Fact]
    public void RcBashEmitsExportAliasAndRawBlock()
    {
        WriteCoreToml("""
            [env]
            EDITOR = "vim"

            [path]
            entries = ["~/bin"]

            [alias]
            gs = "git status"

            [rc.raw_bash]
            body = "shopt -s histappend"
            """);
        var outText = RunRc("bash");

        Assert.Contains("export EDITOR='vim'", outText);
        Assert.Contains("export PATH=\"$HOME/bin:$PATH\"", outText);
        Assert.Contains("alias gs='git status'", outText);
        Assert.Contains("shopt -s histappend", outText);
    }

    [Fact]
    public void RcFishUsesFishSyntaxAndSkipsRawBash()
    {
        WriteCoreToml("""
            [env]
            EDITOR = "vim"

            [path]
            entries = ["~/bin"]

            [rc.raw_bash]
            body = "shopt -s histappend"
            """);
        var outText = RunRc("fish");

        Assert.Contains("set -gx EDITOR 'vim'", outText);
        Assert.Contains("fish_add_path", outText);
        Assert.DoesNotContain("shopt", outText);
    }

    [Fact]
    public void RcUnknownShellFailsCleanly()
    {
        SetHome();
        var stderr = new StringWriter();
        var oldErr = Console.Error;
        Console.SetError(stderr);
        int exit;
        try { exit = new RcApplet().Run(["tcsh"]); }
        finally { Console.SetError(oldErr); }
        Assert.Equal(1, exit);
        Assert.Contains("bash, zsh, fish", stderr.ToString());
    }

    // --- expect overlay ---------------------------------------------------

    [Fact]
    public void ExpectOverlayIsReadThroughSharedConfig()
    {
        SetHome();
        WriteCoreToml("db_path = \"/dev/null\"\n");
        WriteAppletToml("expect", """
            default_timeout = 7
            verbose = true
            quiet = true
            echo = false
            """);
        var cfg = ExpectConfig.Load(verboseDefault: false, quietDefault: false, timeoutDefault: 30);
        Assert.Equal(7, cfg.DefaultTimeoutSeconds);
        Assert.True(cfg.Verbose);
        Assert.True(cfg.Quiet);
        Assert.False(cfg.Echo);
    }

    [Fact]
    public void ExpectOverlayMissingMeansZeroConfigDefaults()
    {
        SetHome();
        WriteCoreToml("db_path = \"/dev/null\"\n");
        var cfg = ExpectConfig.Load(verboseDefault: true, quietDefault: false, timeoutDefault: 45);
        Assert.Equal(45, cfg.DefaultTimeoutSeconds);
        Assert.True(cfg.Verbose);
        Assert.False(cfg.Quiet);
        Assert.True(cfg.Echo);
    }

    [Fact]
    public void ExpectMalformedOverlayFailsLoudly()
    {
        SetHome();
        WriteCoreToml("db_path = \"/dev/null\"\n");
        WriteAppletToml("expect", "this is not toml ====\n");
        // Through the shared config reader: bad TOML is never silently
        // ignored, unlike the old hand-rolled parser.
        Assert.Throws<InvalidDataException>(() => ExpectConfig.Load(false, false, 30));
    }

    // --- ticket -----------------------------------------------------------

    private (int Exit, string Stdout, string Stderr) RunTicket(params string[] args)
    {
        SetHome();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var rc = new TicketApplet().Run(args);
            return (rc, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void TicketAddDefaultsToDefaultQueue()
    {
        var (exit, stdout, _) = RunTicket("add", "alpha task");
        Assert.Equal(0, exit);
        Assert.Contains("[default]", stdout);

        var (_, show, _) = RunTicket("show", "1");
        Assert.Contains("queue:", show);
        Assert.Contains("default", show);
    }

    [Fact]
    public void TicketListFiltersByQueueCaseInsensitively()
    {
        RunTicket("add", "alpha-task", "-q", "dev");
        RunTicket("add", "beta-task", "-q", "work");

        // Bare word on list is the queue filter; matching ignores case.
        var (exit, stdout, _) = RunTicket("list", "DEV");
        Assert.Equal(0, exit);
        Assert.Contains("alpha-task", stdout);
        Assert.DoesNotContain("beta-task", stdout);

        var (_, stdoutQ, _) = RunTicket("list", "-q", "work");
        Assert.Contains("beta-task", stdoutQ);
        Assert.DoesNotContain("alpha-task", stdoutQ);
    }

    [Fact]
    public void TicketMoveReassignsQueue()
    {
        RunTicket("add", "gamma-task", "-q", "dev");
        var (exit, stdout, _) = RunTicket("move", "1", "work");
        Assert.Equal(0, exit);
        Assert.Contains("[work]", stdout);

        var (_, dev, _) = RunTicket("list", "dev");
        Assert.DoesNotContain("gamma-task", dev);
        var (_, work, _) = RunTicket("list", "work");
        Assert.Contains("gamma-task", work);
    }

    [Fact]
    public void TicketQueuesListsOpenCounts()
    {
        RunTicket("add", "one", "-q", "dev");
        RunTicket("add", "two", "-q", "dev");
        RunTicket("add", "three", "-q", "work");
        RunTicket("done", "3");

        var (exit, stdout, _) = RunTicket("queues");
        Assert.Equal(0, exit);
        Assert.Contains("[dev]", stdout);
        Assert.Contains("(2)", stdout);
        Assert.DoesNotContain("[work]", stdout);   // only open tickets counted
    }

    [Fact]
    public void TicketMigratesPreQueueDatabase()
    {
        SetHome();
        // Simulate a database from before the queue column existed.
        var cfg = Config.Load("ticket");
        using (var conn = Db.Open(cfg))
        {
            Db.Exec(conn, """
                CREATE TABLE tickets (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    body TEXT NOT NULL DEFAULT '',
                    priority TEXT NOT NULL DEFAULT 'normal',
                    tags TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL DEFAULT 'open',
                    created TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
                    closed TEXT
                );
                """);
            Db.Exec(conn, "INSERT INTO tickets (title) VALUES ('old ticket')");
        }

        var (exit, stdout, _) = RunTicket("all");
        Assert.Equal(0, exit);
        Assert.Contains("old ticket", stdout);
        Assert.Contains("[default]", stdout);
    }

    [Fact]
    public void TicketCommentAppendsAndEditKeepsComments()
    {
        RunTicket("add", "task");
        Assert.Equal(0, RunTicket("comment", "1", "first", "note").Exit);
        Assert.Equal(0, RunTicket("comment", "1", "second note").Exit);

        var (_, show, _) = RunTicket("show", "1");
        Assert.Contains("comments:", show);
        Assert.Contains("first note", show);
        Assert.Contains("second note", show);

        // edit -d replaces the body only; the comment thread survives.
        Assert.Equal(0, RunTicket("edit", "1", "-d", "new body").Exit);
        var (_, show2, _) = RunTicket("show", "1");
        Assert.Contains("new body", show2);
        Assert.Contains("first note", show2);

        // comments verb lists them standalone
        var (exit, cout, _) = RunTicket("comments", "1");
        Assert.Equal(0, exit);
        Assert.Contains("second note", cout);
    }

    [Fact]
    public void TicketCommentOnMissingTicketFails()
    {
        var (exit, _, stderr) = RunTicket("comment", "9", "hi");
        Assert.Equal(1, exit);
        Assert.Contains("no ticket #9", stderr);
    }

    [Fact]
    public void TicketSearchFindsCommentText()
    {
        RunTicket("add", "some task");
        RunTicket("comment", "1", "uniquecommentword");
        var (exit, stdout, _) = RunTicket("search", "uniquecommentword");
        Assert.Equal(0, exit);
        Assert.Contains("some task", stdout);
    }

    [Fact]
    public void TicketRmRemovesCommentsToo()
    {
        RunTicket("add", "doomed task");
        RunTicket("comment", "1", "thread word");
        Assert.Equal(0, RunTicket("rm", "1").Exit);
        var (exit, _, stderr) = RunTicket("comments", "1");
        Assert.Equal(1, exit);
        Assert.Contains("no ticket #1", stderr);
    }
}

file static class StringExt
{
    public static string[] Lines(this string s) => s.Split('\n');
}
