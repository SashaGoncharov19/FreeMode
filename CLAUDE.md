# CLAUDE.md

Guidance for AI agents working in this repository.

## Read this first

**`docs/HANDOFF.md`** is the live state of the project: what we are doing now, the current blocker, what
was tried, and what to do next. Start there. The long-term plan is `docs/ROADMAP.md`.

## What this is

**FreeMode / GTA Network** — a revival of the 2016–2018 GTA V multiplayer mod. Linux-first: the server,
launcher and headless bot are native **.NET 8**; the game runs through **Proton**. The in-game client
`GTANetwork.dll` is **.NET Framework 4.8** (loaded into `GTA5.exe` by a C++/CLI ScriptHookVDotNet fork).
Browser overlay on **CefSharp / Chromium 151**, client scripting on **ClearScript 7.5 (V8 12)**.

Tested by hand by the owner on **Debian 13 + GTA V Legacy 1.0.3889 under Proton**. There is no Windows box
and no GTA V in CI, so hook/rendering changes are verified in game only, by the owner. The owner writes in
**Ukrainian** — reply in Ukrainian.

## Build & test

```bash
dotnet build GTANetwork.sln -c Release          # everything managed (client compiles on Linux via the SHVDN stub)
eng/dev-build-client.sh --sync                  # dev container: rebuild the client into ~/GTANetwork (~10 s)
eng/dev-test.sh                                 # the Linux CI checks locally (server smoke + bot integration)
```

The dev container (`.devcontainer/`, `docker-compose.yml`) exists so client changes can be tried in game
without waiting for CI. Only the C++/CLI `ScriptHookVDotNet.dll` needs Windows + MSVC. See
`docs/DEVCONTAINER.md`.

## Rules (do not break)

* **Commit and push only to the designated branch** (currently `claude/modernize-deps-4d8uyn`). Never push
  elsewhere without explicit permission.
* **Releases only via the `build.yml` workflow_dispatch**, input `release_tag` (a `-` = pre-release, e.g.
  `v0.2.0-alpha.6`). The matching `CHANGELOG.md` section is the release body. **Never push git tags** (403).
* **Do not open PRs or cut releases unless asked.**
* **ScriptHookV is not redistributable** — never commit it; players download it themselves.
* GitHub access is scoped to `sashagoncharov19/freemode`; `mcp__github__*` tools load via ToolSearch and
  may need reconnecting.
* **No model identifiers in any committed artifact** (commits, PRs, code, docs) — chat only. The session
  supplies the exact commit trailers and GitHub-comment footer; use those.
