---
name: dev-loop
description: Build the in-game client and browser host in the dev container against the real ScriptHookVDotNet and sync them into the owner's install (~/GTANetwork); run the Linux CI checks.
---

# Dev loop

```bash
docker compose run --rm dev eng/dev-build-client.sh --sync      # client + host → ~/GTANetwork (bin/scripts, cef/), page-aligned
docker compose run --rm dev eng/dev-test.sh                     # server smoke + bot integration (the Linux CI job)
docker compose run --rm dev dotnet publish Launcher/GTANetwork.Launcher.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /gtanetwork   # after Launcher/ changes
python3 eng/pe-realign.py --check ~/GTANetwork/cef              # nothing printed = all aligned
```

* The container is used with `docker compose run --rm dev …` (not `up`). `.env` holds `GTAN_INSTALL`.
* "ScriptHookVDotNet reference: real build" must appear in the build output; the stub is not binary compatible.
* Do not run two container builds at once (they share `obj/` of `Shared/`).
* After syncing, tell the owner what to look for in `~/GTANetwork/logs/` (`docs/agents/testing.md`, "In-game verification").
