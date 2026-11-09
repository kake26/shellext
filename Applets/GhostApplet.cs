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
// Placeholders: on the internal path, $0..$9 / ${0..9} in the expansion are
// substituted with positional args ($0 = the ghost name, mirroring sh);
// unconsumed args still append at the end, so placeholder-free ghosts keep
// their append-everything behavior. On the shell path sh provides $0..
// itself, so placeholders work there verbatim.
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

        Placeholders: $1..$9 in the expansion (multi-digit via braces) are
        replaced by the positional arguments — x {Name} "my title" -p high with
        {Name} = "ticket add $1 -t bug" runs:
            ticket add "my title" -t bug -p high
        $0 is the ghost name. Args not referenced by a placeholder are still
        appended at the end. A $N with no matching argument is an error.
        On the shell path /bin/sh provides $0, $1, ... itself.
        """;

    public int Run(string[] args)
    {
        // Seed the in-process depth from a parent x's shell-path ghost so
        // loops that cross process boundaries (a = "x a") stay capped.
        if (_depth == 0 &&
            int.TryParse(Environment.GetEnvironmentVariable("X_GHOST_DEPTH"), out var seeded))
            _depth = Math.Clamp(seeded, 0, MaxDepth);

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

        // Internal re-dispatch: applets stay inside the x process.
        if (registry.TryGet(head, out var target))
        {
            var expanded = Expand(tokens[1..], args, Name, out var err);
            if (expanded is null)
            {
                Console.Error.WriteLine($"x: ghost '{Name}': {err}");
                return 1;
            }
            _depth++;
            try
            {
                return target.Run(expanded);
            }
            finally
            {
                _depth--;
            }
        }

        // Not an applet — treat the expansion as a shell command.
        return RunShell(Name, expansion, args);
    }

    /// Splits an expansion into words the way a shell would: whitespace
    /// separates words, single/double quotes group a phrase (and are
    /// removed), and a backslash escapes the next character. Only the
    /// internal-dispatch path needs this — the shell path hands the raw
    /// expansion to sh, which tokenizes properly on its own.
    /// Unterminated quotes run to the end of the string, leniently.
    internal static string[] Tokenize(string s)
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

    /// Expands $N / ${N} placeholders in the expansion tokens against the
    /// caller's args: $0 is the ghost name (mirroring the shell path, where
    /// sh -c receives the ghost name as $0), $1.. are the args. An arg
    /// referenced by a placeholder is consumed; unconsumed args are appended
    /// after the expansion tokens, so placeholder-free ghosts keep their
    /// append-everything behavior. A referenced $N with no matching arg is
    /// an error, not an empty string. A '$' not followed by a digit or '{'
    /// passes through literally. Note quotes are already stripped by
    /// Tokenize, so '$1' expands too — use the shell path for a literal $.
    internal static string[]? Expand(string[] tokens, string[] args, string name, out string? error)
    {
        var used = new bool[args.Length];
        var result = new List<string>(tokens.Length + args.Length);

        foreach (var token in tokens)
        {
            var sb = new StringBuilder(token.Length);
            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] != '$' || i + 1 >= token.Length)
                {
                    sb.Append(token[i]);
                    continue;
                }

                int n, next;
                if (token[i + 1] == '{')
                {
                    var close = token.IndexOf('}', i + 2);
                    if (close < 0 || !int.TryParse(token[(i + 2)..close], out n) || n < 0)
                    {
                        sb.Append(token[i]);
                        continue;
                    }
                    next = close + 1;
                }
                else if (char.IsDigit(token[i + 1]))
                {
                    n = token[i + 1] - '0';
                    next = i + 2;
                }
                else
                {
                    sb.Append(token[i]);
                    continue;
                }

                if (n == 0)
                {
                    sb.Append(name);
                }
                else if (n - 1 < args.Length)
                {
                    used[n - 1] = true;
                    sb.Append(args[n - 1]);
                }
                else
                {
                    error = $"${n} has no matching argument ({args.Length} given)";
                    return null;
                }
                i = next - 1;
            }
            result.Add(sb.ToString());
        }

        for (var i = 0; i < args.Length; i++)
            if (!used[i])
                result.Add(args[i]);

        error = null;
        return [.. result];
    }

    /// /bin/sh -c '<expansion> "$@"' <ghost name> <args...> — $0 is the ghost
    /// name, $@ carries the caller's arguments, exit code propagates.
    /// X_GHOST_DEPTH carries the depth count into any x the shell respawns,
    /// so ghost loops cannot escape the cap by crossing process boundaries.
    private static int RunShell(string name, string command, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"{command} \"$@\"");
        psi.ArgumentList.Add(name);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.EnvironmentVariables["X_GHOST_DEPTH"] = (_depth + 1).ToString();

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
