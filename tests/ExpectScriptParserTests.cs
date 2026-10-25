// ScriptParser — the DSL reader. Pure file-to-steps parsing;
// these tests pin the documented script syntax.

using Xunit;

public class ExpectScriptParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        $"x-exp-{Guid.NewGuid():N}");
    private string Dir { get { Directory.CreateDirectory(_dir); return _dir; } }

    private string Write(string script)
    {
        var path = Path.Combine(Dir, "test.exp");
        File.WriteAllText(path, script);
        return path;
    }

    [Fact]
    public void ParsesCommandAndArgument()
    {
        var steps = ScriptParser.ParseFile(Write("spawn /bin/sh\n"));
        var step = Assert.Single(steps);
        Assert.Equal("spawn", step.Command);
        Assert.Equal("/bin/sh", step.Arg);
        Assert.Equal(1, step.Line);
    }

    [Fact]
    public void StripsDoubleQuotesFromArguments()
    {
        var steps = ScriptParser.ParseFile(Write("expect \"login:\"\n"));
        Assert.Equal("login:", steps[0].Arg);
    }

    [Fact]
    public void RegexSpecKeepsItsSlashes()
    {
        var steps = ScriptParser.ParseFile(Write("expect /he[l]+o/i\n"));
        Assert.Equal("/he[l]+o/i", steps[0].Arg);
    }

    [Fact]
    public void CommentsAndBlanksAreSkipped()
    {
        var steps = ScriptParser.ParseFile(Write("""
            # a comment line

            spawn sh

            sleep 100   # trailing comment
            """));
        Assert.Equal(2, steps.Count);
        Assert.Equal("spawn", steps[0].Command);
        Assert.Equal("sh", steps[0].Arg);
        Assert.Equal("sleep", steps[1].Command);
        Assert.Equal("100", steps[1].Arg);
        // Line numbers refer to the original file.
        Assert.Equal(3, steps[0].Line);
        Assert.Equal(5, steps[1].Line);
    }

    [Fact]
    public void CommandsAreCaseInsensitive()
    {
        var steps = ScriptParser.ParseFile(Write("SPAWN sh\nExpect eof\n"));
        Assert.Equal("spawn", steps[0].Command);
        Assert.Equal("expect", steps[1].Command);
        Assert.Equal("eof", steps[1].Arg);
    }

    [Fact]
    public void SendlineKeepsItsFullArgument()
    {
        var steps = ScriptParser.ParseFile(Write("sendline hello world\n"));
        Assert.Equal("sendline", steps[0].Command);
        Assert.Equal("hello world", steps[0].Arg);
    }

    // --- comment stripping is quote-aware --------------------------------

    [Fact]
    public void HashInsideQuotesIsData()
    {
        var steps = ScriptParser.ParseFile(Write("expect \"#tag\"   # real comment\n"));
        Assert.Equal("#tag", steps[0].Arg);
    }

    [Fact]
    public void HashInsideARegexIsData()
    {
        var steps = ScriptParser.ParseFile(Write("expect /a#b/\n"));
        Assert.Equal("/a#b/", steps[0].Arg);
    }

    [Fact]
    public void HashWithoutPrecedingWhitespaceIsData()
    {
        var steps = ScriptParser.ParseFile(Write("sendline value#tag\n"));
        Assert.Equal("value#tag", steps[0].Arg);
    }

    [Fact]
    public void UnterminatedQuoteStaysLiteral()
    {
        // No closing quote: nothing is stripped, no silent truncation.
        var steps = ScriptParser.ParseFile(Write("expect \"foo\n"));
        Assert.Equal("\"foo", steps[0].Arg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
