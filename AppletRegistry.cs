// Applet plumbing: the interface every applet implements, and the registry
// that dispatch (hybrid and argv[0] mode) looks names up in.

interface IApplet
{
    string Name { get; }
    string Blurb { get; }          // one line, shown in 'x list'
    string Help { get; }           // multi-line, shown in 'x help <name>'
    int Run(string[] args);
}

class AppletRegistry
{
    private readonly Dictionary<string, IApplet> _applets = new();

    public static AppletRegistry Build()
    {
        var r = new AppletRegistry();

        // Built-ins first: core applets can never be shadowed by plugins.
        r.Add(new ListApplet(r));
        r.Add(new HelpApplet(r));
        r.Add(new NoteApplet());
        r.Add(new InstallApplet());
        r.Add(new RcApplet());
        r.Add(new VersionApplet());

        // Native .so applets from ~/.local/lib/x — islands, C ABI only.
        foreach (var so in SoApplet.Discover())
            r.AddIfAbsent(so);

        // Ghosts (aliases) from core config — last, lowest precedence.
        foreach (var (name, expansion) in Config.LoadGhosts())
            r.AddIfAbsent(new GhostApplet(name, expansion, r));

        return r;
    }

    public void Add(IApplet applet) => _applets[applet.Name] = applet;

    /// Registers a plugin or ghost only if nothing already owns the name —
    /// built-ins and .so applets always win over config-defined names.
    public void AddIfAbsent(IApplet applet)
    {
        if (!_applets.ContainsKey(applet.Name))
            _applets[applet.Name] = applet;
    }

    public bool TryGet(string name, out IApplet applet) => _applets.TryGetValue(name, out applet!);

    public IEnumerable<IApplet> All() => _applets.Values.OrderBy(a => a.Name);

    public void PrintList()
    {
        var width = _applets.Values.Max(a => a.Name.Length);
        Console.WriteLine("x applets:");
        foreach (var a in All())
            Console.WriteLine($"  {a.Name.PadRight(width)}  {a.Blurb}");
    }
}
