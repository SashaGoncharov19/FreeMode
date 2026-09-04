# T-007 — Gamemode template (`gtanetwork create`) and freeroam fully in TypeScript

Status: ready
Epic: E-04 TypeScript
Size: M
Branch: task/T-007-template from the integration branch
Depends on: T-005, T-006
PR: yes

## Goal

`Tools/GTANetwork.Cli` (`gtanetwork create <name>`) writes a resource skeleton — `meta.xml`, `server/index.ts`,
`client/index.ts`, `ui/index.html` + `ui/app.ts`, `tsconfig.json` referencing `types/`, `package.json` with scripts
`check` (`tsc --noEmit`) — and `freeroam` is entirely TS (server + client), the reference gamemode.

## Files

* New: `Tools/GTANetwork.Cli/` (net10.0; `create`, later `check`), `templates/resource/**`.
* Change: `Server/resources/freeroam/**` (finish the port started in T-005/T-006; delete `freeroam.cs`), `README.md`
  ("Write a gamemode" section: create → run → connect), `eng/integration-test.sh` if command texts changed, `.github/workflows/build.yml` (publish the CLI).

## Acceptance criteria

- [ ] `gtanetwork create demo && cd demo && bun run check` passes; the server starts `demo` and a bot receives its greeting.
- [ ] `eng/dev-test.sh` passes with freeroam in TS only.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)
