// Config: core toml + per-applet overlay.
//
// Minimal config surface every applet sees. Core keys are *shared
// resources* — db_path, data_dir. Applet toml overrides core, never the
// reverse.

using Tomlyn.Parsing;
using Tomlyn.Syntax;

sealed class Config
{
    public string DbPath { get; init; } = "";
    public string DataDir { get; init; } = "";
    public IReadOnlyDictionary<string, string> Raw { get; init; }
        = new Dictionary<string, string>();

    public static Config Load(string appletName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var corePath = Path.Combine(home, ".config", "x", "x.toml");
        var appletPath = Path.Combine(home, ".config", "x", "applets", $"{appletName}.toml");

        var dataDirDefault = Path.Combine(home, ".local", "share", "x");

        // Merge order: defaults <- core <- applet overlay
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(corePath))
            foreach (var (k, v) in TomlPairs.Parse(corePath))
                merged[k] = v;
        if (File.Exists(appletPath))
            foreach (var (k, v) in TomlPairs.Parse(appletPath))
                merged[k] = v;

        string Get(string key) => merged.TryGetValue(key, out var v) ? v : "";

        var dataDir = Get("data_dir") is { Length: > 0 } d ? d : dataDirDefault;
        var dbPath = Get("db_path") is { Length: > 0 } p
            ? p
            : Path.Combine(dataDir, "x.db");

        return new Config { DbPath = dbPath, DataDir = dataDir, Raw = merged };
    }

    /// Settings for the rc applet: shell-agnostic rc definitions, read from
    /// the core config only (applet overlays do not apply). Unlike the Raw
    /// surface, this reads arrays too ([path] entries) by walking the
    /// Tomlyn syntax tree directly.
    public static RcConfig LoadRc()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var corePath = Path.Combine(home, ".config", "x", "x.toml");

        var env = new Dictionary<string, string>();
        var pathEntries = new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(corePath))
        {
            var doc = TomlPairs.ParseDocument(corePath);
            foreach (var table in doc.Tables)
            {
                var name = table.Name?.ToString().Trim().ToLowerInvariant();
                switch (name)
                {
                    case "env":
                        foreach (var kv in table.Items)
                            if (TomlPairs.Scalar(kv.Value) is { } v)
                                env[kv.Key!.ToString().Trim()] = v;
                        break;

                    case "path":
                        foreach (var kv in table.Items)
                            if (kv.Value is ArraySyntax arr &&
                                kv.Key!.ToString().Trim().Equals("entries",
                                    StringComparison.OrdinalIgnoreCase))
                                foreach (var item in arr.Items)
                                    if (TomlPairs.Scalar(item.Value) is { } v)
                                        pathEntries.Add(v);
                        break;

                    case "alias":
                        foreach (var kv in table.Items)
                            if (TomlPairs.Scalar(kv.Value) is { } v)
                                aliases[kv.Key!.ToString().Trim()] = v;
                        break;

                    case var n when n is not null && n.StartsWith("rc.raw_"):
                        foreach (var kv in table.Items)
                            if (kv.Key!.ToString().Trim().Equals("body",
                                    StringComparison.OrdinalIgnoreCase) &&
                                TomlPairs.Scalar(kv.Value) is { } v)
                                raw[n["rc.".Length..]] = v;
                        break;
                }
            }
        }

        return new RcConfig
        {
            Env = env,
            PathEntries = pathEntries,
            Aliases = aliases,
            RawBlocks = raw,
        };
    }

    /// Ghosts (aliases) come from the core config only: every key under
    /// [ghosts] is name -> expansion. Applet overlays do not define ghosts.
    public static IReadOnlyDictionary<string, string> LoadGhosts()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var corePath = Path.Combine(home, ".config", "x", "x.toml");

        var ghosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(corePath))
            return ghosts;

        foreach (var (k, v) in TomlPairs.Parse(corePath))
            if (k.StartsWith("ghosts.", StringComparison.OrdinalIgnoreCase))
                ghosts[k["ghosts.".Length..].ToLowerInvariant()] = v;

        return ghosts;
    }
}

/// Shell-agnostic rc definitions consumed by the rc applet, all from the
/// [env], [path], [alias] and [rc.raw_*] tables of the core config.
public sealed class RcConfig
{
    /// Environment variables: NAME -> value.
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// PATH entries, prepended in order (first entry = highest priority).
    public IReadOnlyList<string> PathEntries { get; init; } = [];

    /// Shell aliases: name -> command.
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();

    /// Raw passthrough blocks: "raw_bash" / "raw_zsh" / "raw_fish" -> body.
    public IReadOnlyDictionary<string, string> RawBlocks { get; init; } = new Dictionary<string, string>();
}

/// Flattens a TOML document into "section.key" -> string pairs, walking
/// Tomlyn's syntax model directly (never ToModel, which is reflection-based
/// and breaks under trim/AOT). Strings come back unquoted; ints, bools and
/// floats as text — matching the string-typed Raw config surface.
/// Malformed files throw with parser diagnostics; config is never silently
/// ignored.
static class TomlPairs
{
    public static IEnumerable<(string Key, string Value)> Parse(string path)
    {
        var doc = ParseDocument(path);

        foreach (var kv in doc.KeyValues)
        {
            if (Scalar(kv.Value) is { } v)
                yield return (kv.Key!.ToString().Trim(), v);
        }

        foreach (var table in doc.Tables)
        {
            var prefix = table.Name?.ToString().Trim();
            foreach (var kv in table.Items)
            {
                if (Scalar(kv.Value) is { } v)
                {
                    var key = kv.Key!.ToString().Trim();
                    yield return (prefix is { Length: > 0 } ? $"{prefix}.{key}" : key, v);
                }
            }
        }
    }

    /// Parses a TOML file into the Tomlyn syntax tree, refusing to return a
    /// document with errors. Shared entry point for all config readers.
    public static DocumentSyntax ParseDocument(string path)
    {
        var doc = SyntaxParser.Parse(File.ReadAllText(path), path, validate: true);
        if (doc.HasErrors)
            throw new InvalidDataException($"{path}: invalid TOML:\n{doc.Diagnostics}");
        return doc;
    }

    /// Scalar TOML values only. Arrays, inline tables and datetimes are not
    /// part of the Raw config surface — pairs with those values are skipped.
    internal static string? Scalar(ValueSyntax? v) => v switch
    {
        StringValueSyntax s => s.Value,
        IntegerValueSyntax i => i.Value.ToString(),
        BooleanValueSyntax b => b.Value.ToString(),
        FloatValueSyntax f => f.Value.ToString(),
        _ => null,
    };
}
