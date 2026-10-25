// TomlPairs — the config reader every applet sees. Errors must be loud,
// scalars flattened, arrays left to LoadRc (they are not part of the Raw
// string surface).

using Xunit;

public class TomlPairsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        $"x-tests-{Guid.NewGuid():N}");
    private string Dir { get { Directory.CreateDirectory(_dir); return _dir; } }

    [Fact]
    public void FlattensSectionsAndScalars()
    {
        var path = Write("""
            db_path = "/tmp/x.db"
            max_results = 50
            enabled = true
            ratio = 1.5

            [server]
            host = "localhost"
            port = 8080

            [server.tls]
            cert = "a.crt"
            """);
        var pairs = TomlPairs.Parse(path).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("/tmp/x.db", pairs["db_path"]);
        Assert.Equal("50", pairs["max_results"]);
        Assert.Equal("True", pairs["enabled"]);
        Assert.Equal("1.5", pairs["ratio"]);
        Assert.Equal("localhost", pairs["server.host"]);
        Assert.Equal("8080", pairs["server.port"]);
        Assert.Equal("a.crt", pairs["server.tls.cert"]);
    }

    [Fact]
    public void ArraysAreNotPartOfTheRawSurface()
    {
        var path = Write("""
            [path]
            entries = ["a", "b"]
            plain = "yes"
            """);
        var pairs = TomlPairs.Parse(path).ToDictionary(p => p.Key, p => p.Value);

        Assert.DoesNotContain("path.entries", pairs.Keys);
        Assert.Equal("yes", pairs["path.plain"]);
    }

    [Fact]
    public void MalformedTomlThrowsLoudly()
    {
        var path = Write("this is not toml ====\n");
        var ex = Assert.Throws<InvalidDataException>(() => TomlPairs.Parse(path).ToList());
        Assert.Contains("invalid TOML", ex.Message);
    }

    [Fact]
    public void LoadRcReadsEnvPathAliasAndRawBlocks()
    {
        var path = Write("""
            [env]
            EDITOR = "vim"

            [path]
            entries = ["~/bin", "/opt/bin"]

            [alias]
            gs = "git status"

            [rc.raw_bash]
            body = '''
            shopt -s histappend
            '''

            [rc.raw_fish]
            body = "set -g fish_greeting"
            """);
        // LoadRc reads from the fixed ~/.config/x/x.toml path, so point the
        // test at this file through the same parser by hand instead.
        var doc = TomlPairs.ParseDocument(path);
        var rc = Config.LoadRcFrom(doc);

        Assert.Equal("vim", rc.Env["EDITOR"]);
        Assert.Equal(new[] { "~/bin", "/opt/bin" }, rc.PathEntries);
        Assert.Equal("git status", rc.Aliases["gs"]);
        Assert.Equal("shopt -s histappend", rc.RawBlocks["raw_bash"].Trim());
        Assert.Equal("set -g fish_greeting", rc.RawBlocks["raw_fish"]);
    }

    private string Write(string toml)
    {
        var path = Path.Combine(Dir, "x.toml");
        File.WriteAllText(path, toml);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
