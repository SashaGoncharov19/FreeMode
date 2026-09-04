# T-019 — 3D browsers: pages placed in the world, depth-tested

Status: draft
Epic: E-12 CEF UI
Size: L
Branch: task/T-019-3d-browsers from the integration branch
Depends on: T-012
PR: no

## Goal

`API.createCefBrowser3D(width, height, position, rotation, size)` draws a browser as a quad in the world, occluded by
the world geometry, with mouse hit-testing by ray; used for in-world screens (TV, billboards, ATMs).

## Files

* Read first: `docs/CEF-UPGRADE.md` "3D browsers" (design: quad in the D3D11 hook with the camera matrix, depth test against the
  game's depth buffer by hooking `OMSetRenderTargets`, or the game's render targets), `Client/GUI/DirectXHook/Hook/DX11/DXOverlayEngine.cs`,
  `Client/Javascript/JavascriptHook.cs:1102` (CEF API), `Client/Javascript/CameraManager.cs`.

## Log

* 2026-09-04 22:10 agent — created as draft: the design in CEF-UPGRADE.md needs the depth-buffer decision (hook vs render target) first.

## Result

(empty)
