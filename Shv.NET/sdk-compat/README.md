# ScriptHookV SDK - compatible declarations

`Shv.NET/ScriptHookVDotNet.vcxproj` links against `ScriptHookV.lib` and includes `main.h` /
`NativeCaller.h` from Alexander Blade's ScriptHookV SDK. The SDK download on dev-c.com is protected by
Mod_Security and rejects automated downloads (GitHub Actions runners get HTTP 406), so this folder
provides an equivalent, written from the public API:

* `inc/main.h`, `inc/types.h`, `inc/NativeCaller.h` - the function prototypes, typedefs and the
  `eGameVersion` enum exactly as ScriptHookV exports them (the mangled C++ names must match the real
  `ScriptHookV.dll`, so signatures are not to be "improved").
* `ScriptHookV.stub.cpp` + `ScriptHookV.stub.vcxproj` - a dummy DLL with the same exports; building it
  produces the import library `../sdk/lib/ScriptHookV.lib`. The dummy DLL itself is discarded, the
  game loads the real `ScriptHookV.dll`.
* `install-compat-sdk.ps1` - copies the headers to `../sdk/inc` and builds the import library into
  `../sdk/lib` (run from a Developer PowerShell, or let CI do it).

If you have the official SDK, extract its `inc/` and `lib/` into `Shv.NET/sdk/` instead; CI does that
automatically when the repository variable `SHV_SDK_URL` points to a reachable copy of
`ScriptHookV_SDK_1.0.617.1a.zip`.
