# Roadmap

Superseded on 4 Sept 2026 by **`docs/PLAN.md`** (the MVP plan: targets, versions, epics, order) together with
`docs/DECISIONS.md` (decisions) and `docs/tasks/` (the work items). The phase texts that used to live here were folded
into the epics of `docs/PLAN.md`; the history of what was done is in `CHANGELOG.md` and `docs/HANDOFF.md`.

Kept here because `docs/PLAN.md` E-13 refers to it — the route for the in-game client to modern .NET:

* **Client on modern .NET** (1–3 weeks): the in-game client is .NET Framework 4.8 because
  ScriptHookVDotNet is a C++/CLI shell that hosts the desktop CLR. The route is recompiling that shell with
  `/clr:netcore` (.NET 8/10 + `ijwhost`), AssemblyLoadContext instead of the script AppDomain, and the .NET
  Desktop Runtime in the Proton prefix instead of .NET Framework (which also removes the most fragile install
  step on Linux). CefSharp and ClearScript both support .NET Core, so the browser and script work above is a
  prerequisite, not a throw-away.
