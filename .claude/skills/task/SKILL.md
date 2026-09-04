---
name: task
description: Take a task from docs/tasks (or the one the owner names) and execute it end to end following docs/agents/workflow.md — claim, read, build, change, test, record, commit.
---

# Take a task

1. Read `docs/HANDOFF.md` ("Current state"), then `docs/tasks/README.md`. If the owner named a task, open it; else pick
   the first `ready` task whose `Depends on` are `done` and whose Files do not overlap an `in progress` task.
2. Claim it: `Status: in progress`, Log line `YYYY-MM-DD HH:MM <agent> — started`, commit that line on the task's branch
   (create `task/T-NNN-<slug>` from the integration branch if the header says so).
3. Read the task's Files and the matching `docs/CODEMAP.md` section. For shared code, ask the code graph
   (`get_impact_radius_tool`, `query_graph_tool`) what depends on what you will change.
4. Run the baseline tests for the area (`docs/agents/testing.md`) before changing anything.
5. Implement only what the acceptance criteria need. Decisions not settled by the task: use the default in
   `docs/DECISIONS.md`; if there is none, add the question with a recommended default there, take the default, log it.
6. Test; copy the result lines and numbers into the task's Result. Update `CHANGELOG.md` for user-visible changes,
   `docs/HANDOFF.md` if the project state changed, `docs/CODEMAP.md` if layout changed.
7. If the game is needed to verify: write the Owner check (steps + grep lines), set `needs owner`. Otherwise `done`.
8. Commit with the format in `docs/agents/conventions.md` (no model identifiers; trailers from the session). Do not push
   unless the task or the owner says so. Run `code-review-graph update`.
9. Report to the owner in Ukrainian: what changed, the numbers, what they must check in game, what is next.
