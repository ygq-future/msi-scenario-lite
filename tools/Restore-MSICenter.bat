@echo off
setlocal
:: This file is intentionally ASCII-only.
:: cmd.exe parses batch files with the console OEM code page, so non-ASCII text
:: in a .bat turns into garbage commands on non-matching locales. All Chinese
:: messages are printed by the PowerShell scripts instead.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================================
echo   MSI Center background services and startup items - RESTORE DEFAULTS
echo ============================================================================
echo.

echo [1/3] Restoring MSI Center services to automatic start...
for %%S in ("MSI_Center_Service" "MSI Foundation Service" "MSI Sendevsvc") do (
    echo   - service: %%~S
    sc.exe config "%%~S" start= auto >nul 2>&1
    sc.exe start "%%~S" >nul 2>&1
)

echo.
echo [2/3] Re-enabling MSI Center scheduled tasks...
for %%T in ("\OmApSvcBroker" "\OneDC_Updater") do (
    echo   - task: %%~T
    schtasks /Change /TN "%%~T" /Enable >nul 2>&1
)
schtasks /Run /TN "\OmApSvcBroker" >nul 2>&1

echo.
echo [3/3] Re-enabling the MSI Center UWP startup task...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MSICenter-Startup.ps1" -Enable

echo.
echo ============================================================================
echo [DONE] MSI Center services, UWP startup task and scheduled tasks are
echo        restored to their default state.
echo ============================================================================
pause
