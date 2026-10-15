// Native applets — .so libraries discovered in ~/.local/lib/x.
//
// Islands by design: a .so gets argc/argv and returns an exit code through
// a fixed C ABI. It has no access to x's runtime, registry, config or
// database — like a busybox applet written in C.
//
// Contract: <name>.so must export
//     int x_<name>(int argc, char* argv[])
// argv[0] is the applet name (busybox convention), the remaining entries
// are the user's arguments, and the list is null-terminated.

using System.Runtime.InteropServices;
using System.Text;

class SoApplet(string libPath) : IApplet
{
    public string Name { get; } = Path.GetFileNameWithoutExtension(libPath).ToLowerInvariant();
    public string Blurb => "native applet (.so)";
    public string Help => $"""
        Usage: x {Name} [args]

        Native applet loaded from {libPath}.
        Contract: exports int x_{Name}(int argc, char* argv[]).
        See the applet's own documentation for its arguments.
        """;

    /// Scans the lib dir for one-file-per-applet .so plugins. The directory
    /// not existing is the normal case on a fresh machine — no applets.
    public static IEnumerable<SoApplet> Discover()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var libDir = Path.Combine(home, ".local", "lib", "x");
        if (!Directory.Exists(libDir))
            yield break;

        foreach (var so in Directory.EnumerateFiles(libDir, "*.so"))
            yield return new SoApplet(so);
    }

    public int Run(string[] args)
    {
        nint lib;
        try
        {
            lib = NativeLibrary.Load(libPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"x: cannot load {libPath}: {ex.Message}");
            return 1;
        }

        try
        {
            nint entry;
            try
            {
                entry = NativeLibrary.GetExport(lib, $"x_{Name}");
            }
            catch (EntryPointNotFoundException)
            {
                Console.Error.WriteLine(
                    $"x: {libPath} does not export x_{Name}(int argc, char* argv[])");
                return 1;
            }

            return RunMain(entry, args);
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }

    /// argv = [applet name, ...args, NULL] — UTF-8, like a C main() sees.
    private unsafe int RunMain(nint entry, string[] args)
    {
        var main = (delegate* unmanaged[Cdecl]<int, byte**, int>)entry;
        var argc = args.Length + 1;
        var argv = (nint*)Marshal.AllocHGlobal(sizeof(nint) * (argc + 1));
        var owned = new List<nint>(argc);
        try
        {
            nint Dup(string s)
            {
                var bytes = Encoding.UTF8.GetBytes(s + "\0");
                var p = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                owned.Add(p);
                return p;
            }

            argv[0] = Dup(Name);
            for (var i = 0; i < args.Length; i++)
                argv[i + 1] = Dup(args[i]);
            argv[argc] = 0;

            return main(argc, (byte**)argv);
        }
        finally
        {
            foreach (var p in owned)
                Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal((nint)argv);
        }
    }
}
