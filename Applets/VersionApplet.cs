// x version — print the version this binary was built from.
//
// The csproj <Version> stamps the assembly version at build time; NativeAOT
// preserves that identity metadata, so the running binary knows what it is.
// Tag the corresponding fossil check-in rel-X.Y.Z when releasing.

class VersionApplet : IApplet
{
    public string Name => "version";
    public string Blurb => "print the x version";
    public string Help => "Usage: x version\n\nPrints the version of this binary (from the csproj <Version>, e.g. 1.0.0).";

    public int Run(string[] args)
    {
        var v = typeof(Program).Assembly.GetName().Version;
        Console.WriteLine(v is null ? "x (unknown version)" : $"x {v.ToString(3)}");
        return 0;
    }
}
