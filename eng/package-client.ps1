<#
.SYNOPSIS
  Assembles the Windows client package (the folder the NSIS installer and the zip artifact are made from).

  Layout (matches what the launchers and the in-game client expect):
    GTANLauncher.exe            classic Windows launcher (stage 1)
    GTANetwork.Launcher.exe     cross-platform launcher (ASI-loader flow), optional
    launcher\                   GTANSubprocess.exe + GTANetwork.dll (launcher behaviour) + deps
    bin\ScriptHookVDotNet.dll   the C++/CLI hook (built from Shv.NET)
    bin\*.dll                   native helpers injected/loaded into GTA5.exe (V8, EasyHook, SharpDX effects)
    bin\scripts\                GTANetwork.dll (in-game client) + managed dependencies (ClearScript 7 + its V8)
    cef\                        GTANetwork.CefHost.exe (the browser process) + Chromium Embedded Framework runtime +
                                CefSharp (the output folder of Subprocess\GTANetwork.CefHost)
    images\                     HUD / map / CEF assets
    ui\                         the client's own CEF pages (ui/loader), served as https://gtan/<path>

  ScriptHookV.dll and dinput8.dll are NOT included (not redistributable): users download them from
  http://www.dev-c.com/gtav/scripthookv/ and drop them into bin\.
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Out = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path "artifacts/client"),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Require-File([string]$path) {
    if (-not (Test-Path $path)) { throw "Missing build output: $path" }
    return $path
}

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
foreach ($d in "bin/scripts", "launcher", "cef", "images", "ui", "logs", "resources") {
    New-Item -ItemType Directory -Force -Path (Join-Path $Out $d) | Out-Null
}

# 1. Classic Windows launcher chain
$updater = "$Root/Subprocess/PlayGTANetworkUpdater/bin/$Configuration/net48"
Copy-Item (Require-File "$updater/GTANLauncher.exe") $Out
Copy-Item "$updater/GTANLauncher.exe.config" $Out -ErrorAction SilentlyContinue
Copy-Item (Require-File "$updater/Ionic.Zip.dll") $Out

$subprocessHost = "$Root/Subprocess/PlayGTANetwork/bin/$Configuration/net48"
Copy-Item (Require-File "$subprocessHost/GTANSubprocess.exe") "$Out/launcher"
Copy-Item "$subprocessHost/GTANSubprocess.exe.config" "$Out/launcher" -ErrorAction SilentlyContinue

$behaviour = "$Root/Subprocess/GTANSubprocess/bin/$Configuration/net48"
Require-File "$behaviour/GTANetwork.dll" | Out-Null
Copy-Item "$behaviour/*.dll" "$Out/launcher"
Copy-Item "$behaviour/GTANetwork.dll.config" "$Out/launcher" -ErrorAction SilentlyContinue

# 2. Cross-platform launcher (published separately as a single file), if available
$xplat = "$Root/artifacts/launcher-win-x64/GTANetwork.Launcher.exe"
if (Test-Path $xplat) { Copy-Item $xplat $Out } else { Write-Warning "GTANetwork.Launcher.exe not found ($xplat) - skipping" }

# 3. In-game client + managed dependencies -> bin\scripts
$client = "$Root/Client/bin/$Configuration/net48"
Require-File "$client/GTANetwork.dll" | Out-Null
Copy-Item "$client/*.dll" "$Out/bin/scripts"
Remove-Item "$Out/bin/scripts/ScriptHookVDotNet.dll" -ErrorAction SilentlyContinue  # SHVDN deletes it from scripts\ anyway

# 4. ScriptHookVDotNet (real C++/CLI build) + native helpers -> bin\
Copy-Item (Require-File "$Root/Shv.NET/bin/ScriptHookVDotNet.dll") "$Out/bin"
foreach ($n in "EasyHook64.dll", "EasyLoad64.dll", "sharpdx_direct3d11_effects_x64.dll", "sharpdx_direct3d11_1_effects_x64.dll") {
    Copy-Item (Require-File "$Root/libs/$n") "$Out/bin"
}
# ClearScript 7: the V8 library sits next to ClearScript.Core.dll in bin\scripts (copied above); a second copy in
# bin\ (the game folder) is the fallback for the loader's default search path.
Copy-Item (Require-File "$client/ClearScriptV8.win-x64.dll") "$Out/bin"

Set-Content -Path "$Out/bin/PUT-SCRIPTHOOKV-HERE.txt" -Value @"
GTA Network needs Alexander Blade's ScriptHookV, which may not be redistributed.

1. Download it from http://www.dev-c.com/gtav/scripthookv/
2. Copy  ScriptHookV.dll  and  dinput8.dll  from the archive into this folder (bin\).

The launcher refuses to start until both files are here.
"@

# 5. The browser host and its CEF runtime (CefSharp + Chromium, placed next to GTANetwork.CefHost.exe by the CefSharp
#    NuGet targets), images, data files
$cefSrc = "$Root/Subprocess/GTANetwork.CefHost/bin/$Configuration/net48"
Require-File "$cefSrc/GTANetwork.CefHost.exe" | Out-Null
Require-File "$cefSrc/libcef.dll" | Out-Null
Require-File "$cefSrc/CefSharp.BrowserSubprocess.exe" | Out-Null
Require-File "$cefSrc/CefSharp.Core.Runtime.dll" | Out-Null
Copy-Item "$cefSrc/*" "$Out/cef" -Recurse -Force
Remove-Item "$Out/cef/cache" -Recurse -Force -ErrorAction SilentlyContinue
# CefSharp's debug symbols and XML docs are not needed at runtime (31 MB); keep the locales players actually use (50 MB otherwise).
Get-ChildItem "$Out/cef" -Include CefSharp.*.pdb, *.xml -Recurse | Remove-Item -Force
# Page-align the PE files so that Wine maps them from disk instead of copying each into every process (eng/pe-realign.py:
# 1.5 GB -> 0.86 GB resident for an idle page). Windows does not care either way.
$python = (Get-Command python -ErrorAction SilentlyContinue) ?? (Get-Command python3 -ErrorAction SilentlyContinue)
if ($python) { & $python.Source "$Root/eng/pe-realign.py" "$Out/cef" | Select-Object -Last 1 } else { Write-Warning "python not found: cef\ PE files stay 512-byte aligned (Wine copies them into every process)" }
$keepLocales = "en-US", "en-GB", "uk", "ru", "pl", "de", "fr", "es", "pt-BR", "tr", "it", "nl", "cs", "ro", "hu"
Get-ChildItem "$Out/cef/locales" -Filter *.pak | Where-Object { $keepLocales -notcontains $_.BaseName } | Remove-Item -Force
Copy-Item "$Root/images/*" "$Out/images" -Recurse -Force
Copy-Item "$Root/ui/*" "$Out/ui" -Recurse -Force   # the client's own pages (connect loader), served by the browser host as https://gtan/
Copy-Item "$Root/vehicleData.json", "$Root/whitelist.txt", "$Root/LICENSE" $Out

# 6. Version stamp (read from the in-game client)
$version = (Get-Item "$Out/bin/scripts/GTANetwork.dll").VersionInfo.FileVersion
Set-Content -Path "$Out/version.txt" -Value $version
Set-Content -Path "$Out/bin/depsver.txt" -Value $version

Write-Host "Client package $version assembled in $Out"
Get-ChildItem $Out -Recurse -File | Measure-Object -Property Length -Sum | ForEach-Object { Write-Host ("{0} files, {1:N1} MB" -f $_.Count, ($_.Sum / 1MB)) }
