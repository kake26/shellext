// Ghosts — aliases defined in x's configuration, resolved at dispatch.
// Set in the [ghosts] table of ~/.config/x/x.toml:
//
//     [ghosts]
//     recent = "note search"
//     gs     = "git status -sb"
//
// Hybrid expansion, like a shell alias that lives inside x:
//   - first word is a registered applet -> re-dispatch internally, no
//     shell involved (recent -> note search, with x's args appended)
//   - anything else -> run the expansion via /bin/sh, with x's args
//     appended as "$@"
// Ghost chains (ghost -> ghost) are allowed up to MaxDepth levels, so a
// ghost loop fails with a clear error instead of recursing forever.

using System.Diagnostics;
using System.Text;

class GhostApplet(string name, string expansion, AppletRegistry registry) : IApplet
{
    private const int MaxDepth = 8;
    private static int _depth;

    public string Name { get; } = name;
    public string Blurb => $"ghost: {expansion}";
    public string Help => $"""
        Usage: x {Name} [args]

        Ghost (alias) defined in ~/.config/x/x.toml:

            [ghosts]
            {Name} = "{expansion}"

        Expands to: {expansion}
        Arguments given to 'x {Name}' are appended to the expansion. If the
        first word is a registered applet it runs inside x; otherwise the
        expansion runs via /bin/sh.
        """;

    public int Run(string[] args)
    {
        if (_depth >= MaxDepth)
        {
            Console.Error.WriteLine(
                $"x: ghost expansion deeper than {MaxDepth} levels — ghost loop involving '{Name}'?");
            return 1;
        }

        var tokens = Tokenize(expansion);
        if (tokens.Length == 0)
        {
            Console.Error.WriteLine($"x: ghost '{Name}' has an empty expansion");
            return 1;
        }

        var head = tokens[0].ToLowerInvariant();
        var rest = tokens[1..].Concat(args).ToArray();

        // Internal re-dispatch: applets stay inside the x process.
        if (registry.TryGet(head, out var target))
        {
            _depth++;
            try
            {
                return target.Run(rest);
            }
            finally
            {
                _depth--;
            }
        }

        // Not an applet — treat the expansion as a shell command.
        return RunShell(expansion, args);
    }

    /// Splits an expansion into words the way a shell would: whitespace
    /// separates words, single/double quotes group a phrase (and are
    /// removed), and a backslash escapes the next character. Only the
    /// internal-dispatch path needs this — the shell path hands the raw
    /// expansion to sh, which tokenizes properly on its own.
    /// Unterminated quotes run to the end of the string, leniently.
    private static string[] Tokenize(string s)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var have = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                else word.Append(c);
                continue;
            }

            if (inDouble)
            {
                if (c == '\\' && i + 1 < s.Length) { word.Append(s[++i]); continue; }
                if (c == '"') { inDouble = false; continue; }
                word.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    have = true;
                    break;
                case '"':
                    inDouble = true;
                    have = true;
                    break;
                case '\\':
                    have = true;
                    if (i + 1 < s.Length)
                        word.Append(s[++i]);
                    break;
                case ' ' or '\t':
                    if (have)
                    {
                        tokens.Add(word.ToString());
                        word.Clear();
                        have = false;
                    }
                    break;
                default:
                    word.Append(c);
                    have = true;
                    break;
            }
        }

        if (have)
            tokens.Add(word.ToString());

        return [.. tokens];
    }

    /// /bin/sh -c '<expansion> "$@"' <name> <args...> — $0 is the ghost
    /// name, $@ carries the caller's arguments, exit code propagates.
    private static int RunShell(string command, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"{command} \"$@\"");
        psi.ArgumentList.Add("x");
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p is null)
        {
            Console.Error.WriteLine($"x: could not start /bin/sh for ghost '{command}'");
            return 1;
        }
        p.WaitForExit();
        return p.ExitCode;
    }
}
