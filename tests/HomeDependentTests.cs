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
}

file static class StringExt
{
    public static string[] Lines(this string s) => s.Split('\n');
}
