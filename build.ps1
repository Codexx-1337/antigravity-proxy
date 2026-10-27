$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $root "Antigravity-Proxy.exe"

$args = @(
  "/nologo",
  "/target:winexe",
  "/platform:x64",
  "/optimize+",
  "/langversion:5",
  "/codepage:65001",
  "/out:$out",
  "/win32icon:$root\assets\antigravity.ico",
  "/win32manifest:$root\src\app.manifest",
  "/r:System.dll",
  "/r:System.Core.dll",
  "/r:System.Windows.Forms.dll",
  "/r:System.Drawing.dll",
  "/r:System.Management.dll",
  "/r:System.Security.dll",
  "$root\src\AntigravityProxyManager.cs"
)

& $csc $args
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "Built: $out"
