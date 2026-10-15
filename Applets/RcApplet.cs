// x rc — replace the shell's rc file with a generated block.
//
// The point: define environment, PATH, aliases and raw passthrough blocks
// in shell-agnostic TOML (~/.config/x/x.toml) and let x emit the right
// syntax per shell. bash and zsh share syntax (Bourne family); fish is the
// only real variable.
//
// The real rc file shrinks to a single line:
//     eval "$(x rc bash)"            # ~/.bashrc
//     eval "$(x rc zsh)"             # ~/.zshrc
//     x rc fish | source             # ~/.config/fish/config.fish
//
// Raw blocks: [rc.raw_bash] is emitted for bash AND zsh (Bourne family);
// [rc.raw_zsh] / [rc.raw_fish] only for their exact shell.
//
// Note the distinction from [ghosts]: [alias] defines *shell* aliases that
// exist in the shell; ghosts are x-internal aliases for dispatch.

class RcApplet : IApplet
{
    public string Name => "rc";
    public string Blurb => "emit shell rc content (env/path/alias/raw) from x.toml";
    public string Help => """"
        Usage: x rc [bash|zsh|fish]

        Emits a block of shell code built from ~/.config/x/x.toml:

            [env]
            EDITOR = "vim"

            [path]
            entries = ["~/.local/bin/xlets"]    # prepended, in order

            [alias]
            gs = "git status"                   # shell alias

            [rc.raw_bash]                       # verbatim passthrough,
            body = '''...bash-only...'''         # emitted for bash AND zsh
            [rc.raw_zsh] / [rc.raw_fish]        # exact shell only

        Put a single eval line in the real rc file:
            eval "$(x rc bash)"        # .bashrc
            eval "$(x rc zsh)"         # .zshrc
            x rc fish | source         # config.fish

        With no argument the invoking shell is detected automatically
        (/proc parent name, then ZSH_VERSION/FISH_VERSION env markers).
        """";

    public int Run(string[] args)
    {
        var shell = args.Length > 0 ? args[0].ToLowerInvariant() : DetectShell();
        if (shell is not ("bash" or "zsh" or "fish"))
        {
            Console.Error.WriteLine(
                "x rc: unknown or undetectable shell — pass one explicitly: bash, zsh, fish");
            return 1;
        }

        Emit(Config.LoadRc(), shell);
        return 0;
    }

    private static void Emit(RcConfig cfg, string shell)
    {
        var fish = shell == "fish";

        Console.WriteLine($"# --- x rc ({shell}) — generated from x.toml; do not edit ---");

        if (cfg.Env.Count > 0)
        {
            Section("[env]");
            foreach (var (name, value) in cfg.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Console.WriteLine(fish
                    ? $"set -gx {name} {Sq(value)}"
                    : $"export {name}={Sq(value)}");
        }

        if (cfg.PathEntries.Count > 0)
        {
            Section("[path]");
            if (fish)
            {
                // fish_add_path prepends, so emit in reverse to keep the
                // first entry at highest priority. Idempotent, unlike the
                // bash line.
                foreach (var entry in cfg.PathEntries.Reverse())
                    Console.WriteLine($"fish_add_path \"{Homeify(entry)}\"");
            }
            else
            {
                var joined = string.Join(":", cfg.PathEntries.Select(Homeify));
                Console.WriteLine($"export PATH=\"{joined}:$PATH\"");
            }
        }

        if (cfg.Aliases.Count > 0)
        {
            Section("[alias]");
            foreach (var (name, command) in cfg.Aliases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Console.WriteLine($"alias {name}={Sq(command)}");
        }

        // Raw passthrough. raw_bash serves the whole Bourne family; the
        // other two only their exact shell.
        var wanted = fish
            ? new[] { "raw_fish" }
            : shell == "zsh" ? new[] { "raw_bash", "raw_zsh" } : new[] { "raw_bash" };
        foreach (var block in wanted)
        {
            if (cfg.RawBlocks.TryGetValue(block, out var body))
            {
                Section($"[rc.{block}]");
                Console.WriteLine(body.TrimEnd('\r', '\n'));
            }
        }
    }

    /// The invoking shell is our parent process (or its subshell — same
    /// binary), so /proc names it reliably on Linux. Env markers are the
    /// portable fallback; explicit argument beats both.
    private static string? DetectShell()
    {
        try
        {
            var stat = File.ReadAllText("/proc/self/stat");
            var fields = stat[(stat.LastIndexOf(')') + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ppid = int.Parse(fields[1]);
            var comm = File.ReadAllText($"/proc/{ppid}/comm").Trim();
            if (comm is "bash" or "zsh" or "fish")
                return comm;
        }
        catch { }

        if (Environment.GetEnvironmentVariable("ZSH_VERSION") is { Length: > 0 })
            return "zsh";
        if (Environment.GetEnvironmentVariable("FISH_VERSION") is { Length: > 0 })
            return "fish";
        return null;
    }

    /// Single quotes for both POSIX and fish: nothing expands inside them,
    /// and both shells accept the '\'' idiom for an embedded quote.
    private static string Sq(string s) => $"'{s.Replace("'", "'\\''")}'";

    /// "~" only expands unquoted; "$HOME" expands in double quotes for
    /// every supported shell, so normalize to it.
    private static string Homeify(string entry) =>
        entry.StartsWith("~") ? "$HOME" + entry[1..] : entry;

    private static void Section(string title) => Console.WriteLine($"\n# {title}");
}
