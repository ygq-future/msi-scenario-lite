@echo off
setlocal
:: This file is intentionally ASCII-only.
:: cmd.exe parses batch files with the console OEM code page, so non-ASCII text
:: in a .bat turns into garbage commands on non-matching locales. All Chinese
:: messages are printed by the PowerShell scripts instead.
::
:: Double-click entry point for MSI Scenario Lite:
::   prepare MSIWMIACPI2.dll -> build on demand -> launch the app.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Bootstrap.ps1"
