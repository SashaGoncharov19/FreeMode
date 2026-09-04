# Conventions — code, commits, documentation

## Languages and targets

| Part | Language / target | Notes |
| --- | --- | --- |
| `Server/`, `Launcher/`, `Tools/GTANetwork.Bot`, `Map2Resource/` | C#, .NET 8 (`net8.0`; the upgrade target is in `docs/PLAN.md`) | Runs on Linux; nothing Windows-only without an `OperatingSystem.IsWindows()` guard. |
| `Client/`, `NativeUI/`, `Subprocess/GTANetwork.CefHost`, `Tools/CefHarness` | C#, .NET Framework 4.8 (`net48`) | Loaded into `GTA5.exe` (client) or run under Wine (host, harness). Only APIs that exist in .NET Framework 4.8; JSON is Newtonsoft. |
| `Shared/` | C#, `net48` + `netstandard2.0` | Compiles for both worlds; no platform APIs. |
| `Shv.NET/` | C++/CLI | Windows + MSVC only (CI Windows job). The managed stub `Shv.NET/ref` is for compiling on Linux only and is never shipped. |
| `eng/` | bash, PowerShell, Python 3 | bash for Linux/dev loop, PowerShell for the Windows packaging job, Python for `pe-realign.py`. |
| Client scripts / CEF pages | JavaScript (V8 12 via ClearScript), HTML/CSS/JS in Chromium 151 | TypeScript comes with the plan's TS epic. |

`LangVersion` is `latest` everywhere; on `net48` avoid features that need runtime support (default interface
methods, `Span<T>`-heavy APIs, `System.Text.Json`). `Nullable` is off in the old code; new files in the .NET 8
projects use `#nullable enable` at the top.

## Code

* One type per file for new code; namespaces mirror folders (`GTANetwork.GUI`, `GTANetwork.CefHost`, …).
* Names say what a thing is for, not how it is implemented. No `Manager2`, `Helper`, `Utils2`.
* Threading is written down: every class that is touched by more than one thread says which thread does what
  in its summary comment (see `CEFManager`, `TextureRelay`, `SharedTextureSurface`).
* Nothing on a ScriptHookVDotNet script thread may block for more than a few milliseconds (the watchdog logs ticks
  over 20 ms; the game stalls). Long work goes to a worker thread and hands the result to the tick.
* Logging: client — `LogManager.RuntimeLog` (always), `LogManager.VerboseLog` / `VerboseCefLog` (debug mode),
  `LogManager.CefLog` (browser). Markers: `[PROFILE]` for periodic numbers, `[HITCH]` for long frames. Host —
  `Program.Log` (`logs/CEF-host.log`). Server — the server's logger (see `docs/CODEMAP.md`). No `Console.WriteLine`
  in libraries. Log the first N occurrences of a repeating problem, then stop (see `_errorsLogged` patterns).
* Settings: client settings live in `Shared/PlayerSettings.cs` (serialised to `settings.xml`): a property with an
  XML doc comment that states the default and the effect, the default set in the constructor.
* Protocols: a change to the browser-host wire format bumps `CefHostProtocol.Version`; a change to a network packet
  changes `Shared/` on both sides in the same commit and is covered by the bot (`Tools/GTANetwork.Bot`).
* Dependencies: versions are pinned in `Directory.Build.props`. A new binary dependency goes to `libs/` only when
  there is no NuGet package for the exact tested build; say so in a comment next to the reference. Nothing from
  ScriptHookV is committed. Every DLL/EXE that ships in `cef/` must be page-aligned (`eng/pe-realign.py`; the
  build and packaging scripts do it).
* Comments explain why, and point to the doc section for non-obvious mechanisms (e.g. "see docs/CEF-UPGRADE.md,
  Texture lifetime"). No commented-out code in new files.

## Commits

* Subject ≤ 72 characters, imperative, says what changed where: `Browser host: own ring of shared textures per browser`.
* Body: why (the problem, with the evidence), what was done, what was measured (numbers), what is left. Wrap at 100.
* Attribution trailers exactly as the session specifies. **No model identifiers** anywhere in the commit.
* One task = one or a few commits; no "wip" commits on the integration branch.

## Documentation — and what "no AI slop" means here

Documents in this repository are read by people and agents who have to act on them. They follow these rules:

1. **Facts, then instructions.** A section says what is true (with the file, command or measurement that shows it),
   then what to do. No introductions ("In this document we will…"), no summaries that repeat the section.
2. **Concrete or absent.** Paths, class names, commands, versions, numbers with units and how they were measured.
   If a number is not known, say "not measured" — never estimate it into existence.
3. **No evaluative filler.** Words like robust, seamless, powerful, comprehensive, cutting-edge, leverage, elegant,
   simply, easily, just, ensure, delve carry no information; delete them. Adjectives only when they distinguish
   ("the 512-byte-aligned DLL", not "the problematic DLL").
4. **One source of truth per fact.** State lives in `docs/HANDOFF.md`, plans in `docs/PLAN.md`, decisions in
   `docs/DECISIONS.md`, layout in `docs/CODEMAP.md`, tasks in `docs/tasks/`. Other documents link, they do not
   restate. When a fact changes, change it where it lives.
5. **Dates are absolute** (4 Sept 2026, not "yesterday"); versions are exact (CefSharp 151.3.240, not "the latest").
6. **Write for the reader who has to act.** A test section is a list of commands and expected output. An owner
   check is numbered steps and the log lines to grep. A design section names the alternatives that were rejected and why.
7. **Length is earned.** Say it once. A 40-line document that is correct beats a 400-line one that has to be verified.
8. Language: documents, code and commits in English; chat with the owner in Ukrainian.
