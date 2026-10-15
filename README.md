# x — busybox-style shell extender

A single standalone binary that hosts small command-line "applets" — notes,
and whatever else you add. Dispatch is shell-agnostic: every shell resolves
`PATH` identically, so there is no per-shell plugin code.

    x <applet> [args...]     run an applet
    x list                   list applets
    x help [applet]          help for one or all applets

Built as a NativeAOT binary — starts in milliseconds, no runtime
dependencies, works on a fresh machine.

## Build

AOT settings and binary name are configured in the csproj file, so a plain
publish is already AOT. The Makefile wraps the common modes:

    make build-aot          # or: dotnet publish -c Release

Output: `publish/x` (plus `libe_sqlite3.so`, which must ship alongside
it). Non-AOT publish modes exist in the Makefile for experimentation
(`make build-net`, `build-single`, `build-zip`); they opt out of AOT
explicitly.

The binary must keep the name `x` — argv[0]-mode dispatch (below) uses the
filename to pick the applet.

## Versioning

The csproj `<Version>` stamps the binary; `x version` prints it. Bump the
version for a release and tag the fossil check-in:

    fossil tag add rel-1.0.0 <checkin>

## Dispatch — two modes

**Hybrid mode**

    x note buy milk

**argv[0] mode** — busybox style. `x install` symlinks each applet name
(`note`, `help`, ...) to the same binary; the invoked filename *is* the
applet:

    note buy milk

This works in every shell (bash/zsh/fish/dash/cron) because they all resolve
`PATH` the same way. `x install` is the only shell-aware applet, and it is
opt-in: it creates `~/.local/bin/xlets`, symlinks every applet, and appends
one `PATH` line to detected rc files (`.bashrc`, `.zshrc`, fish config). It
refuses to shadow a name already taken by a different command on `PATH`,
and re-running it refreshes stale symlinks without duplicating rc lines.

## Applets

Three kinds, one registry. Precedence: built-ins > `.so` applets > ghosts —
a config-defined name can never shadow a real applet.

| Kind       | Where defined                       | Example                    |
| ---------- | ----------------------------------- | -------------------------- |
| built-in   | C# class in `Applets/`              | `note`, `list`, `help`     |
| native     | `.so` in `~/.local/lib/x/`           | `echo.so` → applet `echo`  |
| ghost      | `[ghosts]` in `~/.config/x/x.toml`   | `recent = "note search"`   |

`x list` shows all three kinds; `x install` symlinks all three.

### Built-ins

| Applet    | Blurb                                                        |
| --------- | ------------------------------------------------------------ |
| `list`    | list all applets                                             |
| `help`    | help for one or all applets                                  |
| `note`    | quick notes: append, read, search                            |
| `install` | create applet symlinks + PATH entry (opt-in)                  |
| `rc`      | emit shell rc content (env/path/alias/raw) from x.toml        |
| `version` | print the x version                                          |

    x note <text>              append a timestamped note
    x note                     show the last max_results notes (default 20)
    x note search <pattern>    filter notes (case-insensitive)

`max_results` is a per-applet override (`~/.config/x/applets/note.toml`)
capping the default listing; unparseable or non-positive values fall back
to 20. Search always caps at 200.

### The rc applet — your rc file, generated

Define what would normally live in `.bashrc` in shell-agnostic TOML, and
shrink every real rc file to a single line:

```toml
# ~/.config/x/x.toml
[env]
EDITOR = "vim"

[path]
entries = ["~/.local/bin/xlets"]    # prepended, in order

[alias]
gs = "git status"                   # shell alias (see ghosts below for x-internal aliases)

[rc.raw_bash]                       # verbatim passthrough, emitted for
body = '''                          # bash AND zsh (Bourne family)
shopt -s histappend
'''                                  # use ''' (literal): shell code must
                                     # pass through verbatim — """ would
                                     # process \-escapes in your shell code

[rc.raw_zsh]                        # exact shell only
body = "setopt INC_APPEND_HISTORY"

[rc.raw_fish]
body = "set -g fish_greeting"
```

    # ~/.bashrc
    eval "$(x rc bash)"

    # ~/.zshrc
    eval "$(x rc zsh)"

    # ~/.config/fish/config.fish
    x rc fish | source

With no argument the invoking shell is detected automatically (via /proc
parent name; `ZSH_VERSION`/`FISH_VERSION` env markers as fallback) — pass
`bash`, `zsh` or `fish` explicitly to be sure. Putting `~/.local/bin/xlets`
in `[path]` entries supersedes the PATH line that `x install` appends.

Emission details: values are single-quoted (nothing expands inside them);
`~` becomes `$HOME` so it expands in double quotes; for fish, env vars are
`set -gx`, PATH uses `fish_add_path` (idempotent — no duplicates), and
`raw_bash` is *not* emitted.

### Native applets (.so)

Compiled shared libraries discovered in `~/.local/lib/x` — one `.so` per
applet, filename is the applet name. Islands by design: a `.so` sees only
`argc`/`argv` and returns an exit code through a fixed C ABI. It has no
access to x's runtime, registry, config or database.

Contract: `<name>.so` must export

```c
int x_<name>(int argc, char* argv[]);
```

`argv[0]` is the applet name (busybox convention), the remaining entries
are the user's arguments, and the list is null-terminated (UTF-8).

```c
/* hello.c -> hello.so */
#include <stdio.h>
int x_hello(int argc, char **argv) {
    printf("hello %s\n", argc > 1 ? argv[1] : "world");
    return 0;
}
```

    gcc -shared -fPIC -o ~/.local/lib/x/hello.so hello.c

### Ghosts (aliases)

Shell aliases that live inside x, defined in core config:

```toml
# ~/.config/x/x.toml
[ghosts]
recent = "note search"
gs     = "git status -sb"
```

Hybrid expansion:

- First word is a registered applet → re-dispatch internally, no shell
  involved: `x recent milk` → `note search milk`.
- Anything else → the expansion runs via `/bin/sh -c '... "$@"'`, with
  x's arguments appended: `x gs -v` → `git status -sb -v`.

Arguments given to the ghost are appended to the expansion; exit codes
propagate. Ghost chains work (a ghost targeting a ghost) up to 8 levels —
a ghost loop fails with a clear error instead of recursing forever.

Expansions are tokenized like a shell would: quotes group a phrase into
one argument (`recent = "note search 'hello world'"` passes
`hello world` as a single argument), and a backslash escapes the next
character. This matters on the internal-dispatch path; shell-path ghosts
are passed raw to `/bin/sh`, which tokenizes on its own.

## Config

TOML, hand-merged, no reflection:

    ~/.config/x/x.toml               core/shared (db_path, data_dir, [ghosts],
                                     [env], [path], [alias], [rc.raw_*])
    ~/.config/x/applets/<name>.toml  per-applet overrides

Lookup order: applet toml → core toml → hardcoded default. A malformed
file fails loudly with file, line and column — config is never silently
ignored. Ghosts and rc definitions are core-only; applet overlays cannot
define them.

    # ~/.config/x/x.toml
    db_path = "/custom/x.db"

## Storage

One SQLite database at `db_path` (default `~/.local/share/x/x.db`), opened
per applet invocation. No daemon, no network, no external state.

## Adding an applet

Three ways, in decreasing order of ceremony:

**Built-in (rebuild required)**

1. Create `Applets/ThingApplet.cs` implementing `IApplet`:

   ```csharp
   class ThingApplet : IApplet
   {
       public string Name => "thing";
       public string Blurb => "one line for 'x list'";
       public string Help => "Usage: x thing [args]\n\nMulti-line help.";
       public int Run(string[] args) { /* ... */ return 0; }
   }
   ```

2. Register it in `AppletRegistry.Build()` (`AppletRegistry.cs`).

**Native (no rebuild of x)**

Drop `<name>.so` exporting `x_<name>(int argc, char* argv[])` into
`~/.local/lib/x/` — discovered on the next run. Full contract above.

**Ghost (no rebuild, no compile)**

Add a `[ghosts]` entry to `~/.config/x/x.toml`. Nothing to register.

In all three cases the rest follows automatically: `x list`, `x help`,
both dispatch modes, and `x install` symlinks. Adding a built-in requires
a rebuild — the registry is static by design; there is no runtime plugin
loading of managed code.

## Project layout

```
Program.cs              entry + dispatch (hybrid + argv[0] mode)
AppletRegistry.cs       IApplet interface + registry (built-in > .so > ghost)
Config.cs               config surface + Tomlyn reading (Raw, ghosts, rc)
Db.cs                   SQLite connection helper
Applets/                one file per applet (+ GhostApplet, SoApplet)
```

## AOT constraints honored by the code

- No dynamic *managed* assembly loading, no Roslyn scripting, no `dynamic`.
- Built-in applets are plain classes in a static registry.
- Native `.so` applets load via `NativeLibrary` + unmanaged function
  pointers — a C ABI, no runtime access from the plugin side.
- `Microsoft.Data.Sqlite` trims/AOTs clean (bundles native `e_sqlite3`).
- TOML is read via Tomlyn's syntax model — not `ToModel` reflection — so
  parsing stays trim/AOT-safe.
