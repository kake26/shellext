// x — busybox-style shell extender (NativeAOT, single standalone binary)
//
//   x <applet> [args...]     run an applet
//   x list                   list applets
//   x help [applet]          help for one or all applets
//
// Build — NativeAOT is the requirement, not a preference. Fast + standalone.
// There is deliberately no single-file fallback:
//   dotnet publish -c Release /p:PublishAot=true
//
// Constraints the code honors (AOT-safe by design):
//   - No dynamic *managed* assembly loading, no Roslyn scripting, no `dynamic`.
//   - Built-in applets are plain classes in a static registry — adding one =
//     rebuild. One file per applet under Applets/; register in Build().
//   - Native .so applets (~/.local/lib/x/*.so) load via NativeLibrary and a
//     fixed C ABI (int x_<name>(int argc, char* argv[])) — islands with no
//     access to this runtime.
//   - Ghosts ([ghosts] in core toml) are config data re-dispatched like a
//     shell alias — no reflection anywhere.
//   - Microsoft.Data.Sqlite trims/AOTs clean (bundles native e_sqlite3).
//   - Config parsing walks Tomlyn's syntax model directly — no ToModel
//     reflection, so it is trim/AOT-safe.
//
// Config: ~/.config/x/x.toml              — core/shared (db_path, data_dir)
//         ~/.config/x/applets/<name>.toml — per-applet overrides (merged on top)
// Lookup order: applet toml -> core toml -> hardcoded default.
//
// Storage: SQLite at db_path (default ~/.local/share/x/x.db).
// No daemons, no network, no external state. Works on a fresh machine.

var invokedAs = Path.GetFileName(Environment.GetCommandLineArgs().FirstOrDefault() ?? "x");
try
{
    var registry = AppletRegistry.Build();
    return Program.Run(registry, args, invokedAs);
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"x: {ex.Message}");
    return 1;
}

static partial class Program
{
    public static int Run(AppletRegistry registry, string[] args, string invokedAs)
    {
        // argv[0] mode: if we weren't invoked as 'x' (or 'x.exe'), the
        // program name IS the applet. Symlinks point here.
        // argv[0] must come from GetCommandLineArgs, not Environment.ProcessPath:
        // ProcessPath resolves symlinks to the real binary, which would defeat
        // argv[0]-mode dispatch entirely.
        if (invokedAs is not ("x" or "x.exe" or "dotnet"))
        {
            var name = invokedAs.ToLowerInvariant();
            if (registry.TryGet(name, out var viaArgv0))
                return viaArgv0.Run(args);

            Console.Error.WriteLine($"x: unknown applet link: {invokedAs}");
            Console.Error.WriteLine("Run 'x list' to see applets.");
            return 1;
        }

        if (args.Length == 0)
        {
            registry.PrintList();
            return 0;
        }

        var sub = args[0].ToLowerInvariant();
        if (!registry.TryGet(sub, out var applet))
        {
            Console.Error.WriteLine($"x: unknown applet: {sub}");
            Console.Error.WriteLine("Run 'x list' to see applets.");
            return 1;
        }

        return applet.Run(args[1..]);
    }
}
