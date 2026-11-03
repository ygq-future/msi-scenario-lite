@echo off
setlocal
:: This file is intentionally ASCII-only.
:: cmd.exe parses batch files with the console OEM code page, so non-ASCII text
:: in a .bat turns into garbage commands on non-matching locales. All Chinese
:: messages are printed by the PowerShell script instead.
::
:: Double-click menu for the MSI Center UWP startup task.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MSICenter-Startup.ps1" -Menu
if errorlevel 1 pause
