---
name: handoff
description: Update docs/HANDOFF.md (live project state), CHANGELOG.md and the task file at the end of a piece of work so the next agent or the owner can continue without the chat.
---

# Handoff

1. `docs/HANDOFF.md`: rewrite "Current state" to what is true now (branch, what is verified where, what awaits the
   owner); adjust "What is next". Add a dated step paragraph only for a finding that changes how the project works;
   keep the file under ~250 lines — details belong in the task file and the area docs.
2. `CHANGELOG.md`: one line per user-visible change under the version in progress; settings with their defaults.
3. The task file: Result (Changed / Verified / Not done / Owner check), Status, Log line.
4. `docs/DECISIONS.md` if something was decided; `docs/CODEMAP.md` if layout changed.
5. Commit (conventions in `docs/agents/conventions.md`). Report to the owner in Ukrainian with the numbers and the in-game check list.
