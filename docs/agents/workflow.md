# Task workflow — how an agent takes a task from "ready" to "done"

The point of this procedure: an agent (any agent) picks a task file, does the work, records the result, and
never has to ask the owner where to drive. The owner's part is limited to (a) writing or approving tasks and
decisions and (b) verifying in game what cannot be verified without GTA V.

## States

| Status | Meaning |
| --- | --- |
| `draft` | Not ready: a dependency is open or a decision in `docs/DECISIONS.md` is pending. Do not start. |
| `ready` | Everything needed is decided and available. Can be picked. |
| `in progress` | Claimed by one agent. The first line of the Log says who and when. |
| `needs owner` | The code is done as far as it can be verified without the game, or a decision only the owner can make is pending. The task file says exactly what the owner must do. |
| `blocked` | Cannot continue; the Log's last entry says why and what would unblock it. |
| `done` | Acceptance criteria met and verified (including the owner's in-game check when the task needs one). |

## Picking a task

1. Unless the owner named a task, take the first `ready` task in `docs/tasks/` (files are numbered; lower
   number first) whose `Depends on` tasks are all `done`.
2. Two agents must not work on files that overlap. Every task lists its Files; check the `in progress` tasks
   before picking, and pick another task if the file sets intersect.
3. Set `Status: in progress` and append to the Log: `YYYY-MM-DD HH:MM <agent> — started`. Commit that change
   first (one line), so a second agent sees the claim.

## Branch and pull request (D-11)

* The task header names the branch (`Branch:`). Default: `task/T-NNN-<slug>` created from the integration branch named
  in `docs/HANDOFF.md` ("Current state"). Never work on `master` or the integration branch directly.
* Commit to that branch only; push only that branch. Never push tags. Never force-push a branch someone else may have
  fetched (the owner reviews your PR from it).
* When the work is done (or `needs owner`): push and open **one pull request** against the integration branch:
  * title: `T-NNN: <task title>`;
  * body: the task's Result — Goal (one paragraph), Changed (files), Verified (commands and result lines with numbers),
    Owner check (if the game is needed), Not done / follow-ups; a link to `docs/tasks/T-NNN-….md`; the attribution
    footer the session specifies. No model identifiers.
  * `gh pr create --base <integration branch> --head task/T-NNN-<slug> --title … --body-file …` (or the platform's tool).
* The owner reviews and merges. Address review comments on the same branch. After the merge, set the task `done`
  (a one-line commit on the integration branch is fine, or the next task's PR carries it).

## Doing the work

1. Read the task's Files, then the code they point to, then the relevant section of `docs/CODEMAP.md`.
   Before changing anything in `Shared/`, the protocol, `Server/API.cs` or the client `ScriptContext`, ask the code
   graph for callers and impact (`query_graph`, `get_impact_radius`, or `code-review-graph detect-changes --brief`).
2. Build and run the existing tests for the area first (`docs/agents/testing.md`), so you know the baseline.
3. Implement the smallest change that meets every acceptance criterion. Non-goals stay out even when tempting.
4. If you hit a decision the task does not settle:
   * if `docs/DECISIONS.md` has a default for it — use the default and write one Log line;
   * if not — write the question with the options and your recommended default into `docs/DECISIONS.md`
     ("Open" table), choose the recommended default, continue, and mention it in the task's Result. Only a
     decision that would make the work useless if wrong (a protocol format the owner must approve, spending
     money, deleting user data) sets the task to `needs owner` and stops.
5. Keep numbers: every performance or sync change is accompanied by a measurement (ms, bytes/s, frames/s,
   players) with the command that produced it. "Faster" without a number is not a result.
6. Every user-visible change gets a line in `CHANGELOG.md` (section: the version being worked on).
7. Every new setting: default in code, one sentence in the setting's XML doc comment, one line in CHANGELOG.

## Finishing

Fill the task's Result section:

* **Changed** — files (paths) and what changed in each, one line per file.
* **Verified** — the commands you ran and their result lines (copy the numbers).
* **Not done / follow-ups** — anything from Scope that is not delivered, and why; new tasks you created for it.
* **Owner check** (if the task touches the hook, rendering, input, sync or anything else that needs the game):
  numbered steps, the exact log lines to look for (`grep` commands), and what "good" and "bad" look like.

Then:

1. `docs/HANDOFF.md` — update "Current state" (what is now true) and "What is next" (what this task changed
   about the order). Keep HANDOFF under ~250 lines: history goes to the task files and `CHANGELOG.md`.
2. `docs/CODEMAP.md` — if you added or moved a project, directory or an entry point.
3. Set `Status: done` (or `needs owner`), add the Log line, commit. Commit message format: `docs/agents/conventions.md`.
4. Push the task branch and open the pull request (above).
5. Run `code-review-graph update` so the graph matches the tree (the Claude Code hook does this automatically).

## Writing a new task

Copy `docs/tasks/TEMPLATE.md`, take the next number, fill every section. A task is `ready` only when:
its Files exist (or are named as new), the Approach names concrete classes/messages/settings, every acceptance
criterion is checkable by a command or by an owner step, and every decision it depends on is in `docs/DECISIONS.md`
as decided. Tasks are small enough to finish in one session (rule of thumb: touches under ~15 files); bigger work is
split into a chain with `Depends on`.

## What agents must not do

* Ask the owner "what next" or "which option" in chat — write it into the task/DECISIONS and continue with the default.
* Widen a task's scope, refactor unrelated code, or "clean up" files the task does not list.
* Mark `done` with failing or skipped tests, or with an acceptance criterion unmet.
* Change the owner's install (`~/GTANetwork`) other than through `eng/dev-sync-client.sh` and the publish command in
  `docs/agents/environment.md`; never edit the game folder or the Wine prefix.
* Commit binaries that are not ours to redistribute (ScriptHookV), model identifiers, secrets, `.env`.
