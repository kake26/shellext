// x list — one-line index of every registered applet.

class ListApplet(AppletRegistry registry) : IApplet
{
    public string Name => "list";
    public string Blurb => "list all applets";
    public string Help => "Usage: x list\n\nLists every applet with a one-line description.";

    public int Run(string[] args)
    {
        registry.PrintList();
        return 0;
    }
}
