// GhostApplet.Expand — $N/${N} placeholder substitution on the internal
// re-dispatch path. Referenced args are consumed; the rest still append.

using Xunit;

public class GhostExpandTests
{
    private static string[]? Expand(string[] tokens, string[] args, out string? error) =>
        GhostApplet.Expand(tokens, args, "g", out error);

    [Fact]
    public void NoPlaceholdersAppendsAllArgs()
    {
        var r = Expand(["note", "search"], ["hello", "world"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "note", "search", "hello", "world" }, r);
    }

    [Fact]
    public void PositionalPlaceholderIsSubstituted()
    {
        var r = Expand(["add", "$1", "-t", "bug"], ["my title"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "add", "my title", "-t", "bug" }, r);
    }

    [Fact]
    public void UnreferencedArgsStillAppendAtTheEnd()
    {
        // x g "my title" -p high with g = "ticket add $1 -t bug"
        var r = Expand(["add", "$1", "-t", "bug"], ["my title", "-p", "high"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "add", "my title", "-t", "bug", "-p", "high" }, r);
    }

    [Fact]
    public void MultiplePlaceholdersConsumeInOrder()
    {
        var r = Expand(["add", "$1", "-d", "$2", "-t", "$3"], ["t", "b", "x"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "add", "t", "-d", "b", "-t", "x" }, r);
    }

    [Fact]
    public void BraceFormSupportsMultiDigit()
    {
        var args = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k" };
        var r = Expand(["x", "${10}", "${11}"], args, out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "x", "j", "k", "a", "b", "c", "d", "e", "f", "g", "h", "i" }, r);
    }

    [Fact]
    public void DollarZeroIsTheGhostName()
    {
        var r = Expand(["echo", "$0"], ["arg"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "echo", "g", "arg" }, r);
    }

    [Fact]
    public void PlaceholderInsideAWordSubstitutes()
    {
        var r = Expand(["pre$1post"], ["X"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "preXpost" }, r);
    }

    [Fact]
    public void RepeatedPlaceholderConsumesTheArgOnce()
    {
        var r = Expand(["$1", "$1"], ["only"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "only", "only" }, r);
    }

    [Fact]
    public void MissingArgIsAnErrorNotAnEmptyString()
    {
        var r = Expand(["add", "$2"], ["only-one"], out var err);
        Assert.Null(r);
        Assert.Contains("$2", err);
    }

    [Fact]
    public void DollarNotFollowedByDigitPassesThrough()
    {
        var r = Expand(["$HOME", "$", "$-"], ["a"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "$HOME", "$", "$-", "a" }, r);
    }

    [Fact]
    public void MalformedBraceFormPassesThroughLiterally()
    {
        var r = Expand(["${oops", "${-1}"], [], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "${oops", "${-1}" }, r);
    }

    [Fact]
    public void ArgWithSpacesStaysOneToken()
    {
        // Tokenize already grouped it; Expand must not re-split.
        var r = Expand(["add", "$1"], ["two words"], out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "add", "two words" }, r);
    }
}
