# Antigravity Proxy Manager

Windows tray utility that forces Antigravity through an HTTP or SOCKS5 proxy without touching the system proxy. Single-file C# WinForms tool; no install needed.

## Files

- `src/AntigravityProxyManager.cs` — main source
- `src/app.manifest` — Windows manifest
- `build.ps1` — build script
- `assets/antigravity.ico` — icon
- `settings.example.ini` — sample proxy config

## Build

```powershell
.\build.ps1
```

This produces `Antigravity-Proxy.exe`.

## Configuration

Proxy settings live only in `settings.ini`; it is ignored by git. Use `settings.example.ini` as a starting point, then set your own proxy through the UI.

## What it does

- Starts Antigravity with `--proxy-server`.
- Adds `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` / `AG_LS_PROXY` / `NO_PROXY`.
- Keeps the system proxy untouched.
- Can auto-start and stay in the tray.
- Can stop Antigravity when the proxy is unreachable.
