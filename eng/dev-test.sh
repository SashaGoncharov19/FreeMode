#!/usr/bin/env bash
# Run the same checks the Linux CI job runs, locally: build the whole solution, then start a real
# server and exercise it with the headless bot over the actual protocol. Lets you validate server,
# launcher, bot and shared-protocol changes in under a minute instead of waiting for CI.
#
# Usage: eng/dev-test.sh
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

art="$(mktemp -d)"
trap 'rm -rf "$art"' EXIT

echo "== Building the solution (Release) =="
dotnet build GTANetwork.sln -c Release -nologo

echo "== TypeScript typings (Tools/GTANetwork.TypeGen) =="
refs="$(find ~/.nuget/packages/microsoft.netframework.referenceassemblies.net48 -type d -path '*/build/.NETFramework/v4.8' | head -1)"
dotnet run --project Tools/GTANetwork.TypeGen -c Release -- \
  --client "$(ls Client/bin/Release/net*/GTANetwork.dll | head -1)" \
  --server "$(ls Server/bin/Release/net*/GTANetworkServer.dll | head -1)" \
  --net48-refs "$refs" --probe "$(dirname "$(ls Shv.NET/ref/bin/Release/net48/ScriptHookVDotNet.dll | head -1)")" --out types --runtime-lib runtime/gtan
if ! git diff --quiet -- types/ runtime/gtan/api.generated.d.ts runtime/gtan/enums.generated.ts; then echo "note: types/ changed - commit the regenerated typings (CI fails on stale typings)"; fi
if command -v bun >/dev/null 2>&1; then
  (cd samples/ts-resource && bun install --frozen-lockfile && bun run check)
else
  echo "bun not found: skipping the TypeScript sample check"
fi

echo "== Launcher GUI (Avalonia, headless self-test) =="
dotnet run --project Launcher.Gui/GTANetwork.Launcher.Gui.csproj -c Release --no-build -- --install-dir "$art" --self-test

echo "== Publishing server + bot (linux-x64) =="
dotnet publish Server/GTANetworkServer.csproj -c Release -r linux-x64 --self-contained true  -o "$art/server" -v quiet
dotnet publish Tools/GTANetwork.Bot/GTANetwork.Bot.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$art/bot" -v quiet
dotnet publish Tools/GTANetwork.Cli/GTANetwork.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$art/cli" -v quiet
cp vehicleData.json "$art/server/"

echo "== Server smoke test =="
eng/smoke-test-server.sh "$art/server"
echo "== Bot integration tests =="
eng/integration-test.sh "$art/server" "$art/bot/GTANetwork.Bot"
eng/integration-test-auth.sh "$art/server" "$art/bot/GTANetwork.Bot"
eng/integration-test-template.sh "$art/server" "$art/bot/GTANetwork.Bot" "$art/cli/gtanetwork"

echo "All local checks passed."
