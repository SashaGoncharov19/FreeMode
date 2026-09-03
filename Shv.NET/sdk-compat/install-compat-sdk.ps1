<#
.SYNOPSIS
  Populates Shv.NET/sdk (inc/ + lib/ScriptHookV.lib) from the compatible declarations in this folder.
  Requires MSBuild with the C++ toolset (run from a Developer PowerShell, or after microsoft/setup-msbuild in CI).
#>
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$sdk = Join-Path $here "..\sdk"

New-Item -ItemType Directory -Force -Path (Join-Path $sdk "inc"), (Join-Path $sdk "lib") | Out-Null
Copy-Item (Join-Path $here "inc\*.h") (Join-Path $sdk "inc") -Force

msbuild (Join-Path $here "ScriptHookV.stub.vcxproj") /nologo /v:minimal /p:Configuration=Release /p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Building the ScriptHookV import library failed" }

$lib = Join-Path $sdk "lib\ScriptHookV.lib"
if (-not (Test-Path $lib)) { throw "Import library was not produced: $lib" }

# The dummy DLL must never leave this machine.
Remove-Item (Join-Path $here "bin") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Compatible ScriptHookV SDK installed into $((Resolve-Path $sdk).Path)"
Get-ChildItem $sdk -Recurse -File | Select-Object -ExpandProperty FullName
