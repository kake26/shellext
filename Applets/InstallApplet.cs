// x install — the only shell-aware applet, and opt-in.
// Creates a private bin dir (~/.local/bin/xlets), symlinks every applet
// to the x binary, and appends one PATH line to the user's rc files.
// Detects bash/zsh (.bashrc/.zshrc) and fish (config/fish) — anything else
// just prints the PATH line for manual setup.
//
// Refuses to shadow: if a name already exists on PATH as a *different*
// executable, it warns and skips that symlink rather than hijacking it.

class InstallApplet : IApplet
{
    public string Name => "install";
    public string Blurb => "create applet symlinks + PATH entry (opt-in shell setup)";
    public string Help => """
        Usage: x install

        Sets up argv[0]-mode applets for your shell:
          1. Creates ~/.local/bin/xlets
          2. Symlinks each applet name to the x binary
          3. Appends a PATH export to detected rc files (.bashrc, .zshrc,
             fish config). For other shells, prints the line to add.

        Skips any name already taken by a different command on PATH.
        Run again after rebuilding x with new applets — existing symlinks
        are refreshed, rc lines are never duplicated.
        """;

    private const string BinDirName = "xlets";
    private const string PathMarker = "# added by x install";

    public int Run(string[] args)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binDir = Path.Combine(home, ".local", "bin", BinDirName);
        var selfPath = Environment.ProcessPath!;
        var registry = AppletRegistry.Build();

        Directory.CreateDirectory(binDir);

        int made = 0, skipped = 0;
        foreach (var applet in registry.All().Where(a => a.Name is not "install"))
        {
            var link = Path.Combine(binDir, applet.Name);
            if (NameTakenByOther(applet.Name, selfPath))
            {
                Console.Error.WriteLine($"skipped {applet.Name}: already on PATH elsewhere");
                skipped++;
                continue;
            }
            if (File.Exists(link) || Directory.Exists(link))
                File.Delete(link); // refresh stale links after rebuilds
            File.CreateSymbolicLink(link, selfPath);
            made++;
        }

        Console.WriteLine($"symlinked {made} applet(s) into {binDir} ({skipped} skipped)");
        return SetupPath(home, binDir);
    }

    private static bool NameTakenByOther(string name, string selfPath)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (!File.Exists(candidate)) continue;
                // It's us (or a link to us) — fine, we own it.
                var real = Path.GetFullPath(candidate);
                if (real == Path.GetFullPath(selfPath)) continue;
                if (IsLinkToUs(candidate, selfPath)) continue;
                return true;
            }
            catch (IOException) { }
        }
        return false;
    }

    private static bool IsLinkToUs(string linkPath, string selfPath)
    {
        try
        {
            var target = File.ResolveLinkTarget(linkPath, true);
            return target != null &&
                   Path.GetFullPath(target.FullName) == Path.GetFullPath(selfPath);
        }
        catch (IOException) { return false; }
    }

    private static int SetupPath(string home, string binDir)
    {
        var line = $"export PATH=\"{binDir}:$PATH\"  {PathMarker}";

        var rcCandidates = new[]
        {
            Path.Combine(home, ".bashrc"),
            Path.Combine(home, ".zshrc"),
            Path.Combine(home, ".config", "fish", "config.fish"),
        };

        int touched = 0;
        foreach (var rc in rcCandidates.Where(File.Exists))
        {
            var content = File.ReadAllText(rc);
            if (content.Contains(binDir))
            {
                touched++;
                continue;
            }
            // fish uses different syntax
            var fishLine = $"fish_add_path {binDir}  {PathMarker}";
            File.AppendAllText(rc, $"\n{(rc.EndsWith(".fish") ? fishLine : line)}\n");
            touched++;
            Console.WriteLine($"added PATH to {rc}");
        }

        if (touched == 0)
            Console.WriteLine($"no rc files found — add to PATH manually:\n  {line}");

        Console.WriteLine("restart your shell, then applets are commands (e.g. 'note hi')");
        return 0;
    }
}
