// x help — full help text for one applet, or all of them.

class HelpApplet(AppletRegistry registry) : IApplet
{
    public string Name => "help";
    public string Blurb => "help for one or all applets";
    public string Help => "Usage: x help [applet]\n\nWith no applet name, prints help for all applets.";

    public int Run(string[] args)
    {
        if (args.Length == 0)
        {
            foreach (var a in registry.All())
            {
                Console.WriteLine($"x {a.Name} — {a.Blurb}");
                Console.WriteLine(a.Help);
                Console.WriteLine();
            }
            return 0;
        }

        if (!registry.TryGet(args[0].ToLowerInvariant(), out var applet))
        {
            Console.Error.WriteLine($"x help: unknown applet: {args[0]}");
            return 1;
        }

        Console.WriteLine(applet.Help);
        return 0;
    }
}
