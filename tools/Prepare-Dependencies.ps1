<#
.SYNOPSIS
    Copies the MSI-provided binaries required by MSI Scenario Lite from a local
    MSI Center installation.

.DESCRIPTION
    This project does not redistribute MSI's proprietary binaries. They are
    copied from the copy of MSI Center already installed on this machine.
    Nothing is downloaded.

    Source files:
        <MSI Center>\MSIWMIACPI2.dll
        <MSI NBFoundation Service>\ResetUEFIVariable.exe
        <MSI NBFoundation Service>\UEFIVaribleDll.dll
#>
[CmdletBinding()]
param(
    [string]$MsiCenterDir = 'C:\Program Files (x86)\MSI\MSI Center',
    [string]$MsiFoundationDir = 'C:\Program Files (x86)\MSI\MSI NBFoundation Service'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot | Split-Path -Parent

$sources = @(
    @{ Path = Join-Path $MsiCenterDir 'MSIWMIACPI2.dll'; Name = 'MSIWMIACPI2.dll' }
    @{ Path = Join-Path $MsiFoundationDir 'ResetUEFIVariable.exe'; Name = 'ResetUEFIVariable.exe' }
    @{ Path = Join-Path $MsiFoundationDir 'UEFIVaribleDll.dll'; Name = 'UEFIVaribleDll.dll' }
)

$copied = 0
foreach ($source in $sources) {
    if (Test-Path $source.Path) {
        Copy-Item $source.Path (Join-Path $root $source.Name) -Force
        Write-Host "Copied $($source.Name)" -ForegroundColor Green
        $copied++
    }
    else {
        Write-Warning "Not found: $($source.Path)"
    }
}

if ($copied -eq 0) {
    throw 'No dependencies were copied. Install MSI Center, or pass -MsiCenterDir / -MsiFoundationDir explicitly.'
}

Write-Host ''
Write-Host "Done ($copied/3). These files are git-ignored and must not be redistributed."
