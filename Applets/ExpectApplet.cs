// ExpectApplet.cs — "expect" applet for x (shellext).
//
// Script-driven, Expect-style automation: spawn a child process, wait for
// patterns in its output (stdout+stderr into one buffer), send responses.
// Pure BCL: Process, Regex, StringBuilder — NativeAOT-safe, trims clean,
// zero new dependencies, zero SQLite usage.
//
// Tier: built-in (Tier 1) — per island principle test: it wants no shared
// runtime services, but it's tiny, core-flavored, and belongs in `x list`
// alongside help/list. The standalone Expect.CLI canvas remains the
// reference version; this is the x-native port.
//
// Usage:
//   x expect <script.exp> [-v] [-q] [-t <seconds>]
//   x expect -l | --list-commands     show DSL reference
//
// Config overlay (~/.config/x/applets/expect.toml), all optional:
//   default_timeout = 30        # seconds, used when no -t and no `timeout` step
//   verbose = false            # default for -v
//   quiet = false              # default for -q
//   echo = true                # pass matched/consumed text through to stdout
//
// Script DSL (line-based, # = comment, blank lines ignored):
//   spawn <program> [args...]   start child (one at a time per script)
//   expect "<literal>"          wait for literal text
//   expect /<regex>/            wait for regex; /…/i = ignore case
//   expect eof                  wait for child exit; captures exit code
//   send <text>                 raw text, no newline
//   sendline <text>             text + newline
//   set <name> = <value>        variable; expanded in later steps as ${name}
//   timeout <seconds>           timeout for the NEXT expect step only
//   sleep <ms>                  pause script
//   log <msg>                   write to stderr
//   fail <msg>                  abort: script error, exit code 1
//
// Regex capture groups from the last `expect /…/` are available as ${1}, ${2}, …
// Exit codes: 0 child's code (after expect eof) or success; 1 script error;
//             2 timeout; 3 child died before match; 64 usage.
//
// No pty: full-screen TUI programs (vim/ncurses) won't behave. Fine for
// prompts, menus, installers, ssh password prompts, CLI sessions.
//
// Integration notes:
//   - Register in the applet registry next to RcApplet:
//       registry.AddIfAbsent("expect", new ExpectApplet());
//     (AddIfAbsent preserves precedence: built-ins > .so > ghosts.)
//   - Name note: `expect` may collide with Tcl Expect on PATH. The symlink
//     farm's refuse-to-shadow skips it, so `expect` resolves to Tcl on PATH
//     but `x expect` always works. If a PATH-resolvable name is wanted,
//     register as "xexpect" and add a ghost: xexpect = "x expect".
//   - Reads its overlay via the shared layered-TOML config (applets/expect.toml)
//     if x exposes a typed reader; the reader below degrades to defaults
//     when no overlay exists, honoring zero-config.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


class ExpectApplet : IApplet
{
    public string Name => "expect";

    public string Blurb => "script-driven interactive automation (expect-style)";

    public string Help => """
            Usage: x expect <script.exp> [-v] [-q] [-t <seconds>]

            Drives an interactive program: waits for patterns in its output,
            sends responses. stdout and stderr are watched together.

            Options:
              -v, --verbose     log every expect/send and matches to stderr
              -q, --quiet       don't echo child output as it is consumed
              -t, --timeout <s> default timeout in seconds (default 30)
              -l, --list-commands
                                print the script DSL reference

            Exit codes: 0 ok (child's code after `expect eof`), 1 script error,
                        2 timeout, 3 child exited before match, 64 usage.

            Config overlay (~/.config/x/applets/expect.toml), all optional:
              default_timeout = 30
              verbose = false
              quiet = false
            """;

        public int Run(string[] args)
        {
            // ---- flag parsing -------------------------------------------------
            string? scriptPath = null;
            bool verbose = false, quiet = false;
            int timeoutSec = 30;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-v": case "--verbose": verbose = true; break;
                    case "-q": case "--quiet": quiet = true; break;
                    case "-l": case "--list-commands": Console.WriteLine(DslReference); return 0;
                    case "-t": case "--timeout":
                        if (++i >= args.Length || !TryPosInt(args[i], out timeoutSec))
                        { Console.Error.WriteLine("expect: -t needs a positive integer"); return 64; }
                        break;
                    default:
                        if (scriptPath is null && !args[i].StartsWith('-')) scriptPath = args[i];
                        else { Console.Error.WriteLine($"expect: unknown option '{args[i]}'"); return 64; }
                        break;
                }
            }

            if (scriptPath is null)
            {
                Console.Error.WriteLine("usage: x expect <script.exp> [-v] [-q] [-t <seconds>]");
                return 64;
            }
            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"expect: script not found: {scriptPath}");
                return 66;
            }

            // ---- overlay config (optional, zero-config defaults) --------------
            var cfg = ExpectConfig.Load(verbose, quiet, timeoutSec);
            verbose = cfg.Verbose; quiet = cfg.Quiet; timeoutSec = cfg.DefaultTimeoutSeconds;

            var steps = ScriptParser.ParseFile(scriptPath);
            return ScriptRunner.RunAsync(steps, verbose, quiet, timeoutSec)
                                .GetAwaiter().GetResult();
        }

        private const string DslReference = """
            expect script commands:
              spawn <program> [args...]   start the child process
              expect "<literal>"           wait for literal text
              expect /<regex>/             wait for regex (/.../i = ignore case)
              expect eof                   wait for child exit
              send <text>                  send raw text
              sendline <text>              send text + newline
              set <name> = <value>         define variable, used as ${name}
              timeout <seconds>            timeout for next expect only
              sleep <ms>                   pause script
              log <msg>                    write to stderr
              fail <msg>                   abort with script error
            regex groups from last expect are available as ${1}, ${2}, ...
            """;

        private static bool TryPosInt(string s, out int v)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0;
    }

    // ------------------------------------------------------------------
    // Config overlay — applets/expect.toml, optional.
    // ------------------------------------------------------------------
    public sealed class ExpectConfig
    {
        public int DefaultTimeoutSeconds = 30;
        public bool Verbose;
        public bool Quiet;

        public static ExpectConfig Load(bool verboseDefault, bool quietDefault, int timeoutDefault)
        {
            var cfg = new ExpectConfig
            { Verbose = verboseDefault, Quiet = quietDefault, DefaultTimeoutSeconds = timeoutDefault };
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile), ".config/x/applets/expect.toml");
                if (!File.Exists(path)) return cfg;
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Split('#')[0].Trim();
                    if (line.Length == 0) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key = line[..eq].Trim();
                    var val = line[(eq + 1)..].Trim();
                    switch (key)
                    {
                        case "default_timeout":
                            if (int.TryParse(val, out int s) && s > 0) cfg.DefaultTimeoutSeconds = s;
                            break;
                        case "verbose": cfg.Verbose = ParseBool(val, cfg.Verbose); break;
                        case "quiet": cfg.Quiet = ParseBool(val, cfg.Quiet); break;
                    }
                }
            }
            catch { /* config is an overlay, never a crash */ }
            return cfg;
        }

        private static bool ParseBool(string s, bool fallback)
            => bool.TryParse(s, out var b) ? b : fallback;
    }

    // ------------------------------------------------------------------
    // Child session: process + rolling buffer + expect/send primitives.
    // ------------------------------------------------------------------
    public sealed class ExpectSession : IDisposable
    {
        private readonly Process _proc;
        private readonly StringBuilder _buffer = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Task _pumpOut, _pumpErr;
        private readonly bool _echo;

        private ExpectSession(Process proc, bool echo)
        {
            _proc = proc;
            _echo = echo;
            // stdout AND stderr into one buffer: prompts can arrive on either.
            _pumpOut = PumpAsync(_proc.StandardOutput);
            _pumpErr = PumpAsync(_proc.StandardError);
        }

        public static ExpectSession Spawn(string file, string args, bool echo)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = new Process { StartInfo = psi };
            if (!proc.Start())
                throw new InvalidOperationException($"failed to start '{file}'");
            return new ExpectSession(proc, echo);
        }

        /// <summary>Wait for regex; consumes through end of match; returns it.</summary>
        public async Task<Match> ExpectMatchAsync(Regex regex, TimeSpan timeout, string label)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Match? hit = null;
                await _lock.WaitAsync();
                try
                {
                    var all = _buffer.ToString();
                    var m = regex.Match(all);
                    if (m.Success)
                    {
                        var consumed = all[..(m.Index + m.Length)];
                        _buffer.Remove(0, m.Index + m.Length);
                        if (_echo) Console.Write(consumed);
                        hit = m;
                    }
                }
                finally { _lock.Release(); }
                if (hit is not null) return hit;

                if (sw.Elapsed > timeout)
                    throw new ExpectTimeoutException(label, timeout, await SnapshotAsync());
                if (_proc.HasExited && await IsDrainedAsync())
                    throw new ExpectExitedException(_proc.ExitCode, label, await SnapshotAsync());
                await Task.Delay(20);
            }
        }

        public async Task SendLineAsync(string line = "")
        { await _proc.StandardInput.WriteLineAsync(line); await _proc.StandardInput.FlushAsync(); }

        public async Task SendAsync(string text)
        { await _proc.StandardInput.WriteAsync(text); await _proc.StandardInput.FlushAsync(); }

        public async Task<int> ExpectExitAsync(TimeSpan timeout)
        {
            var t = _proc.WaitForExitAsync();
            if (await Task.WhenAny(t, Task.Delay(timeout)) != t)
                throw new ExpectTimeoutException("eof", timeout, await SnapshotAsync());
            return _proc.ExitCode;
        }

        public async Task<string> SnapshotAsync()
        {
            await _lock.WaitAsync();
            try { return _buffer.ToString(); }
            finally { _lock.Release(); }
        }

        private async Task<bool> IsDrainedAsync()
        {
            // small settle so late output isn't mistaken for a dead child
            var a = await SnapshotAsync();
            await Task.Delay(50);
            return a == await SnapshotAsync();
        }

        public void Dispose()
        {
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
            try { _pumpOut.Wait(500); } catch { }
            try { _pumpErr.Wait(500); } catch { }
            _proc.Dispose();
            _lock.Dispose();
        }

        private async Task PumpAsync(StreamReader reader)
        {
            var buf = new char[4096];
            try
            {
                int n;
                while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await _lock.WaitAsync();
                    try { _buffer.Append(buf, 0, n); }
                    finally { _lock.Release(); }
                }
            }
            catch (ObjectDisposedException) { /* killed */ }
        }
    }

    public sealed class ExpectTimeoutException : Exception
    {
        public ExpectTimeoutException(string pattern, TimeSpan timeout, string buffer)
            : base($"timed out after {timeout.TotalSeconds:0.#}s waiting for {Show(pattern)}\n" +
                   $"--- buffer ---\n{buffer}\n--- end ---") { }

        private static string Show(string p) => p.Length <= 60 ? $"\"{p}\"" : $"\"{p[..60]}…\"";
    }

    public sealed class ExpectExitedException : Exception
    {
        public ExpectExitedException(int code, string pattern, string buffer)
            : base($"child exited (code {code}) before matching \"{pattern}\"\n" +
                   $"--- buffer ---\n{buffer}\n--- end ---") { }
    }

    // ------------------------------------------------------------------
    // Script parsing + execution.
    // ------------------------------------------------------------------
    public sealed class ScriptStep
    {
        public string Command = "", Arg = "";
        public int Line;
    }

    public static class ScriptParser
    {
        public static List<ScriptStep> ParseFile(string path)
        {
            var steps = new List<ScriptStep>();
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Split('#')[0].Trim();
                if (line.Length == 0) continue;

                int sp = line.IndexOf(' ');
                var cmd = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
                var arg = sp < 0 ? "" : line[(sp + 1)..].Trim();
                if (arg.Length >= 2 && arg[0] == '"') arg = arg[1..^1];
                steps.Add(new ScriptStep { Command = cmd, Arg = arg, Line = i + 1 });
            }
            return steps;
        }
    }

    public static class ScriptRunner
    {
        public static async Task<int> RunAsync(List<ScriptStep> steps, bool verbose, bool quiet, int defaultTimeoutSec)
        {
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExpectSession? session = null;
            var nextTimeout = TimeSpan.FromSeconds(defaultTimeoutSec);
            int exitCode = 0;

            string Expand(string text)
            {
                foreach (var kv in vars) text = text.Replace("${" + kv.Key + "}", kv.Value);
                return text;
            }

            try
            {
                foreach (var step in steps)
                {
                    var arg = Expand(step.Arg);
                    switch (step.Command)
                    {
                        case "spawn":
                        {
                            if (session is not null)
                                throw new ScriptError(step.Line, "spawn: a process is already running");
                            int sp = arg.IndexOf(' ');
                            var file = sp < 0 ? arg : arg[..sp];
                            var rest = sp < 0 ? "" : arg[(sp + 1)..];
                            session = ExpectSession.Spawn(file, rest, echo: !quiet);
                            if (verbose) Console.Error.WriteLine($"[spawn] {file} {rest}");
                            break;
                        }

                        case "expect":
                        {
                            if (session is null) throw new ScriptError(step.Line, "expect: no spawned process");
                            if (arg == "eof")
                            {
                                exitCode = await session.ExpectExitAsync(nextTimeout);
                                if (verbose) Console.Error.WriteLine($"[eof] exit code {exitCode}");
                                break;
                            }
                            var rx = PatternToRegex(arg);
                            var m = await session.ExpectMatchAsync(rx, nextTimeout, arg);
                            for (int g = 1; g < m.Groups.Count; g++)
                                vars[g.ToString(CultureInfo.InvariantCulture)] = m.Groups[g].Value;
                            if (verbose) Console.Error.WriteLine($"[expect] matched {arg}");
                            break;
                        }

                        case "send":
                        case "sendline":
                        {
                            if (session is null) throw new ScriptError(step.Line, $"{step.Command}: no spawned process");
                            if (step.Command == "sendline") await session.SendLineAsync(arg);
                            else await session.SendAsync(arg);
                            if (verbose) Console.Error.WriteLine($"[{step.Command}] {arg}");
                            break;
                        }

                        case "set":
                        {
                            int eq = arg.IndexOf('=');
                            if (eq < 0) throw new ScriptError(step.Line, "set: expected 'name = value'");
                            vars[arg[..eq].Trim()] = arg[(eq + 1)..].Trim();
                            break;
                        }

                        case "timeout":
                        {
                            if (!int.TryParse(arg, out var s) || s <= 0)
                                throw new ScriptError(step.Line, $"timeout: '{arg}' is not a positive integer");
                            nextTimeout = TimeSpan.FromSeconds(s);
                            break;
                        }

                        case "sleep":
                        {
                            if (!int.TryParse(arg, out var ms) || ms < 0)
                                throw new ScriptError(step.Line, $"sleep: '{arg}' is not a non-negative integer");
                            await Task.Delay(ms);
                            break;
                        }

                        case "log": Console.Error.WriteLine(arg); break;

                        case "fail": throw new ScriptError(step.Line, arg.Length > 0 ? arg : "explicit failure");

                        default: throw new ScriptError(step.Line, $"unknown command '{step.Command}'");
                    }
                }
            }
            catch (ExpectTimeoutException ex)
            { Console.Error.WriteLine($"expect: {ex.Message}"); return 2; }
            catch (ExpectExitedException ex)
            { Console.Error.WriteLine($"expect: {ex.Message}"); return 3; }
            catch (ScriptError ex)
            { Console.Error.WriteLine($"expect: script error at line {ex.Line}: {ex.Message}"); return 1; }
            finally { session?.Dispose(); }

            return exitCode;
        }

        private static Regex PatternToRegex(string spec)
        {
            // "/pattern/" or "/pattern/i"; anything else = literal match
            if (spec.Length > 2 && spec.StartsWith('/'))
            {
                bool ic = spec.EndsWith("/i", StringComparison.Ordinal);
                if (spec.EndsWith('/')) return new Regex(spec[1..^1], RegexOptions.Compiled);
                if (ic) return new Regex(spec[1..^2], RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
            return new Regex(Regex.Escape(spec), RegexOptions.Compiled);
        }
    }

    public sealed class ScriptError : Exception
    {
        public int Line { get; }
        public ScriptError(int line, string message) : base(message) => Line = line;
    }