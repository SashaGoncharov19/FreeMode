# T-021 — Spike: mounting DLC packs at runtime (no game restart)

Status: draft
Epic: E-08 DLC packs
Size: L
Branch: task/T-021-dlc-runtime-mount from the integration branch
Depends on: T-014
PR: yes

## Goal

Answer with evidence whether a `dlc.rpf` can be mounted into the running game on build 1.0.3889 from the SHVDN C++/CLI
shell (`Shv.NET/`, hooks on RAGE `fiDevice`/`fiPackfile` and the DLC content manager), how many hours it took, and what
breaks on a game update. Outcome: a decision line in `docs/DECISIONS.md` (do it / do not) and, if positive, a task chain.

## Files

* Read: `Shv.NET/source/**` (the shell; where hooks live), `docs/CEF-UPGRADE.md` (how the team verifies hook work — only in game by the owner).

## Log

* 2026-09-04 23:00 agent — created as draft (D-10: after T-014/T-022, M4).

## Result

(empty)
