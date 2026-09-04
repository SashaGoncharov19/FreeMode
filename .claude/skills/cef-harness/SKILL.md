---
name: cef-harness
description: Run the browser host acceptance test under Proton without the game (Tools/CefHarness via eng/cef-harness.sh): page render, bridge, latency, shared textures, benchmarks; read its logs.
---

# CEF harness

Runs on the host machine (needs Proton and the game's Wine prefix); refuses while GTA V runs.

```bash
eng/cef-harness.sh --build                           # build harness + host in the container first (after code changes)
eng/cef-harness.sh                                   # shared-memory frames: pixels, resourceCall, eval→frame latency, resize
eng/cef-harness.sh --shared-texture                  # GPU + the host's texture ring, read back cross-process, latency
eng/cef-harness.sh --shared-texture --bench 8 --size 1280x720   # throughput: texture events/s, copies/s, CPU
eng/cef-harness.sh --install-cef                     # the host installed in ~/GTANetwork instead of the build output
```

Exit code 0 = pass; the `RESULT:` line is the verdict. Logs in `Tools/CefHarness/bin/Release/net48/`: `harness.log`
(the harness), `harness-host.log` (the host), `harness-chromium.log`, `harness-wine.log` (Wine; `e0434352` from
`xalia.exe` is Proton's accessibility helper, not ours). Record the latency and bench lines in the task file.
