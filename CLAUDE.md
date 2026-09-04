# CLAUDE.md

Read `AGENTS.md` first: it is the operating manual for every agent in this repository (what this is, the reading
order, the rules, the task loop, where results go). This file only adds what is specific to Claude Code.

* **Skills** (`.claude/skills/`): `/task` takes a task from `docs/tasks/` end to end; `/dev-loop` builds the client and
  browser host into the owner's install; `/cef-harness` runs the browser acceptance test under Proton; `/handoff`
  updates `docs/HANDOFF.md`, `CHANGELOG.md` and the task file; `/release` is the only allowed release procedure
  (only when the owner asks).
* **Code graph**: the `code-review-graph` MCP server is configured in `.mcp.json`; a hook keeps the graph updated
  after edits. Query it before changing `Shared/`, the protocols, `Server/API.cs` or the client `ScriptContext`.
* **The owner writes Ukrainian — reply in Ukrainian.** Code, docs and commits stay in English.
* **Attribution**: the session specifies the commit trailer and the GitHub comment footer; use exactly those. No
  model identifiers in any committed artifact.
* GitHub access is scoped to `sashagoncharov19/freemode`; `mcp__github__*` tools load via ToolSearch and may need
  reconnecting. Pushing tags returns 403 by design; never work around it.
* Memory notes (`~/.claude/projects/.../memory/`) are for facts about the owner's machine and preferences that are not
  derivable from the repository; project state goes to `docs/HANDOFF.md`, not to memory.
