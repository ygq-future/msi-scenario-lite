<#
.SYNOPSIS
    Copies the MSI-provided WMI ACPI wrapper required by MSI Scenario Lite from a
    local MSI Center installation.

.DESCRIPTION
    This project does not redistribute MSI's proprietary binaries. It copies
    MSIWMIACPI2.dll from the copy of MSI Center already installed on this machine.
    Nothing is downloaded.

    Source file:
        <MSI Center>\MSIWMIACPI2.dll
#>
[CmdletBinding()]
param(
    [string]$MsiCenterDir = 'C:\Program Files (x86)\MSI\MSI Center'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot | Split-Path -Parent

$sourcePath = Join-Path $MsiCenterDir 'MSIWMIACPI2.dll'
if (-not (Test-Path $sourcePath)) {
    throw "Not found: $sourcePath. Install MSI Center, or pass -MsiCenterDir explicitly."
}

Copy-Item $sourcePath (Join-Path $root 'MSIWMIACPI2.dll') -Force
Write-Host 'Copied MSIWMIACPI2.dll' -ForegroundColor Green
Write-Host 'Done (1/1). This file is git-ignored and must not be redistributed.'
