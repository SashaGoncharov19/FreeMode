# T-NNN — <title: what will be true when this is done>

Status: draft | ready | in progress | needs owner | blocked | done
Epic: <E-N name from docs/PLAN.md>
Size: S (hours) | M (a day) | L (several days; consider splitting)
Branch: <task/T-NNN-slug from <integration branch>, or the branch the owner named>
Depends on: <T-NNN, …> or none
PR: yes (one PR per task against the integration branch, D-11)

## Goal

One paragraph. What exists afterwards, stated so that it can be checked.

## Why

The user-visible or developer-visible effect; the plan epic this serves; the evidence that it is needed
(a measurement, a log, an owner report).

## Scope

* In: …
* Out (non-goals): …

## Files

* Read: `path` — why it matters.
* Change: `path` — what changes.
* New: `path` — what it contains.

## Approach

1. Numbered, concrete steps: classes, messages, settings, versions, commands. An engineer who has never seen the
   repository should be able to follow them.
2. …

## Acceptance criteria

- [ ] Each criterion is checkable by a command, a log line or an owner step.
- [ ] …

## Test plan

Commands and the expected result lines (see `docs/agents/testing.md`). Owner steps if the game is needed.

## Risks and notes

What can go wrong, what was considered and rejected, links to the relevant doc sections.

## Log

* YYYY-MM-DD HH:MM <agent> — started / decided X (default from DECISIONS.md) / measured Y / blocked by Z.

## Result

* **Changed**: `path` — what.
* **Verified**: command → result line.
* **Not done / follow-ups**: …
* **Owner check**: numbered steps + grep lines (if needed).
