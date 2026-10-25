// GhostApplet.Tokenize — pure, edge-case-rich word splitting. These tests
// pin the shell-alias semantics ghosts rely on.

using Xunit;

public class TokenizeTests
{
    [Theory]
    [InlineData("a b c", new[] { "a", "b", "c" })]
    [InlineData("  a   b  ", new[] { "a", "b" })]
    [InlineData("a\tb", new[] { "a", "b" })]
    [InlineData("", new string[] { })]
    [InlineData("   ", new string[] { })]
    [InlineData("one", new[] { "one" })]
    public void PlainWords(string input, string[] expected) =>
        Assert.Equal(expected, GhostApplet.Tokenize(input));

    [Fact]
    public void SingleQuotesGroupAPhrase() =>
        Assert.Equal(new[] { "note", "search", "hello world" },
            GhostApplet.Tokenize("note search 'hello world'"));

    [Fact]
    public void DoubleQuotesGroupAPhrase() =>
        Assert.Equal(new[] { "note", "search", "hello world" },
            GhostApplet.Tokenize("note search \"hello world\""));

    [Fact]
    public void QuotesAreRemoved() =>
        Assert.Equal(new[] { "hello" }, GhostApplet.Tokenize("'hello'"));

    [Fact]
    public void EmptyQuotesMakeAnEmptyArgument() =>
        Assert.Equal(new[] { "a", "" }, GhostApplet.Tokenize("a ''"));

    [Fact]
    public void BackslashEscapesASpace() =>
        Assert.Equal(new[] { "back slash" }, GhostApplet.Tokenize(@"back\ slash"));

    [Fact]
    public void BackslashEscapesAQuoteInsideDoubleQuotes() =>
        Assert.Equal(new[] { "say \"hi\"" },
            GhostApplet.Tokenize("\"say \\\"hi\\\"\""));

    [Fact]
    public void NothingExpandsInsideSingleQuotes() =>
        Assert.Equal(new[] { "$HOME" }, GhostApplet.Tokenize("'$HOME'"));

    [Fact]
    public void AdjacentQuotedFragmentsJoinIntoOneWord() =>
        Assert.Equal(new[] { "abcdefgh" }, GhostApplet.Tokenize("ab'cd'\"ef\"gh"));

    [Fact]
    public void UnterminatedQuoteRunsToTheEnd() =>
        // Lenient by design: an apostrophe in prose starts a quote that
        // swallows the rest of the string.
        Assert.Equal(new[] { "dont stop" }, GhostApplet.Tokenize("don't stop"));
}
