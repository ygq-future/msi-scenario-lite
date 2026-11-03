<#
.SYNOPSIS
    Builds MsiScenarioLite.exe using the .NET Framework compiler shipped with Windows.

.DESCRIPTION
    No Visual Studio / .NET SDK required. Requires MSIWMIACPI2.dll in the
    repository root - run tools\Prepare-Dependencies.ps1 first.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fwDir  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpfDir = Join-Path $fwDir 'WPF'

if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc. .NET Framework 4.x is required."
}

$dependencies = @('MSIWMIACPI2.dll')
$missing = $dependencies | Where-Object { -not (Test-Path (Join-Path $root $_)) }
if ($missing) {
    throw "Missing dependency: $($missing -join ', '). Run tools\Prepare-Dependencies.ps1 first."
}

$out = Join-Path $root 'MsiScenarioLite.exe'

$arguments = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:anycpu'
    "/win32icon:$(Join-Path $root 'Assets\app.ico')"
    "/win32manifest:$(Join-Path $root 'app.manifest')"
    "/out:$out"
    "/r:$(Join-Path $wpfDir 'WindowsBase.dll')"
    "/r:$(Join-Path $wpfDir 'PresentationCore.dll')"
    "/r:$(Join-Path $wpfDir 'PresentationFramework.dll')"
    "/r:$(Join-Path $fwDir 'System.Xaml.dll')"
    "/r:$(Join-Path $fwDir 'System.Windows.Forms.dll')"
    "/r:$(Join-Path $fwDir 'System.Drawing.dll')"
    "/r:$(Join-Path $root 'MSIWMIACPI2.dll')"
    (Join-Path $root 'MsiScenarioLite.cs')
)

& $csc $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Built $out" -ForegroundColor Green
Write-Host "Runtime layout: MsiScenarioLite.exe + Assets\ + MSIWMIACPI2.dll (all in this folder)."
