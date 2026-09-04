# AGENTS.md — how to work in this repository

Entry point for any coding agent (Claude Code, Codex, Cursor, …) and for people joining the project. Claude Code
also reads `CLAUDE.md`, which adds only Claude-specific notes. Everything here is normative.

## 1. What this is

FreeMode / GTA Network: a revival of the 2016–2018 GTA V multiplayer mod, Linux-first. The dedicated server, the
launcher and the headless bot are .NET 10; the game runs through Proton.
The in-game client `GTANetwork.dll` is .NET Framework 4.8, loaded into `GTA5.exe` by a C++/CLI ScriptHookVDotNet
fork in a second AppDomain. The browser is Chromium 151 (CefSharp) in a separate process
(`cef/GTANetwork.CefHost.exe`); client scripting is JavaScript on ClearScript 7.5 (V8 12). Tested by hand by the
owner on Debian 13 + GTA V Legacy 1.0.3889 under Proton; there is no Windows machine and no GTA V in CI.

## 2. Read in this order

1. `docs/HANDOFF.md` — live state: integration branch, what is verified where, what awaits the owner, what is next.
2. `docs/PLAN.md` — the MVP plan: epics, order, targets. `docs/DECISIONS.md` — decided and open decisions.
3. `docs/tasks/README.md` and the task you will take.
4. `docs/CODEMAP.md` — where things live. The code graph (§6) answers "who calls this, what does a change touch".
5. `docs/agents/workflow.md` (the task procedure), `conventions.md` (code, commits, how to write docs),
   `testing.md` (what to run for which change), `environment.md` (machines, install layout, logs, commands).
6. Area documents when the task touches them: `docs/CEF-UPGRADE.md` (browser), `docs/SYNC.md` (synchronisation),
   `docs/DEVCONTAINER.md` (dev container), `CHANGELOG.md`.

## 3. Rules

* **Branches and pull requests** (D-11): work on `task/T-NNN-<slug>`, created from the integration branch named in
  `docs/HANDOFF.md`; push only that branch; finish with one pull request against the integration branch (format in
  `docs/agents/workflow.md`). Never commit to `master` or the integration branch directly, never push tags (403 by
  design), never force-push, never merge your own PR.
* **No releases** unless the owner asks. Releases only through `build.yml` workflow_dispatch with `release_tag`
  (`.claude/skills/release/SKILL.md`).
* **ScriptHookV is not redistributable**: never commit it. Nothing from the owner's game folder enters the repository.
* **No model identifiers** in code, docs, commit messages or PR text. Attribution trailers exactly as the session gives them.
* **Language**: code, comments, docs, commits in English; chat with the owner in Ukrainian.
* **Do not ask the owner where to drive.** The task file, `docs/PLAN.md` and `docs/DECISIONS.md` decide. An undecided
  question gets an entry in `docs/DECISIONS.md` with a recommended default; take the default and continue
  (`docs/agents/workflow.md`, "Doing the work", step 4). Stop only for a decision that would make the work useless if
  wrong, or for in-game verification (status `needs owner`).
* **Tests are not negotiable**: never weaken or skip one to get green; a feature that must be disabled is logged and documented.
* **The owner's install (`~/GTANetwork`)** is changed only through `eng/dev-sync-client.sh` and the publish command in
  `docs/agents/environment.md`. Never touch the game folder or the Wine prefix by hand.
* **Numbers, not adjectives**: performance and sync work ships with a measurement and the command that produced it.

## 4. The task loop (short form)

1. Pick the first `ready` task whose dependencies are `done` and whose Files do not overlap an `in progress` task (or the
   task the owner named). Set `in progress`, add the Log line, commit the claim.
2. Read the task's Files, the code, the CODEMAP section; query the code graph before changing shared code.
3. Baseline: run the tests for the area (`docs/agents/testing.md`).
4. Change the minimum that satisfies every acceptance criterion; non-goals stay out.
5. Test; record commands and result lines in the task's Result.
6. Record: task file (Result, Status, Log) → `CHANGELOG.md` (user-visible change) → `docs/HANDOFF.md` (state) →
   `docs/DECISIONS.md` (decisions) → `docs/CODEMAP.md` (layout).
7. Commit on the task branch (format: `docs/agents/conventions.md`), push it, open the PR (`docs/agents/workflow.md`,
   "Pull request"). The owner merges; then set the task `done`.
8. Needs the game? Write the Owner check (steps + `grep` lines) into the task and the PR and set `needs owner`.

## 5. Commands

```bash
docker compose run --rm dev dotnet build GTANetwork.sln -c Release   # everything managed (client against the SHVDN stub)
docker compose run --rm dev eng/dev-build-client.sh --sync           # client + browser host against the real SHVDN → ~/GTANetwork
docker compose run --rm dev eng/dev-test.sh                          # the Linux CI checks: server smoke + bot integration
eng/cef-harness.sh [--build] [--shared-texture] [--bench N --size WxH]   # browser host under Proton, no game (host machine)
```

The dev container (`.devcontainer/`, `docker-compose.yml`, `.env` with `GTAN_INSTALL`) is the build environment; the
harness runs on the host machine. Details and the log files: `docs/agents/environment.md`.

## 6. Code graph (code-review-graph)

The repository is indexed with `code-review-graph` (tree-sitter graph of files, types, functions, calls, imports;
SQLite in `.code-review-graph/`, git-ignored). Setup per machine: `pip install code-review-graph` (in a venv on
Debian: `python3 -m venv ~/.local/share/code-review-graph && ~/.local/share/code-review-graph/bin/pip install code-review-graph`),
then `code-review-graph build` in the checkout; `code-review-graph update` after edits (Claude Code runs it from a
hook). The MCP server is configured in `.mcp.json`; other tools: `code-review-graph install --platform <name>`.
Use it for: `get_architecture_overview_tool` (orientation), `query_graph_tool` (callers/callees of a symbol),
`get_impact_radius_tool` / `detect_changes_tool` (what a change touches, before committing),
`semantic_search_nodes_tool` (find a thing by name). CLI equivalents: `code-review-graph detect-changes --brief`,
`code-review-graph visualize --format svg`.

## 7. Where results go

| What | Where |
| --- | --- |
| Task status, log, verification, owner check | the task file in `docs/tasks/` |
| Project state (what is true now, what is next) | `docs/HANDOFF.md` |
| User-visible change, new setting | `CHANGELOG.md` |
| A decision or an open question | `docs/DECISIONS.md` |
| New/moved project, directory, entry point | `docs/CODEMAP.md` |
| A lasting measurement | the task file and the area doc (`docs/SYNC.md`, `docs/CEF-UPGRADE.md`) |
| A fact about the owner's machine or install | `docs/agents/environment.md` |
