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
echo   MSI Center background services and startup items - DISABLE ALL
echo ============================================================================
echo.

echo [1/4] Stopping and disabling MSI Center background services...
for %%S in ("MSI_Center_Service" "MSI Foundation Service" "MSI Sendevsvc") do (
    echo   - service: %%~S
    sc.exe stop "%%~S" >nul 2>&1
    sc.exe config "%%~S" start= disabled >nul 2>&1
)

echo.
echo [2/4] Disabling the MSI Center UWP startup task (Settings ^> Apps ^> Startup)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MSICenter-Startup.ps1" -Disable

echo.
echo [3/4] Disabling MSI Center scheduled tasks...
for %%T in ("\OmApSvcBroker" "\OneDC_Updater") do (
    echo   - task: %%~T
    schtasks /Change /TN "%%~T" /Disable >nul 2>&1
)

echo.
echo [4/4] Terminating remaining MSI Center processes...
for %%P in ("DCv2.exe" "MSI_Central_Service.exe" "MSI.CentralServer.exe" "MSI.TerminalServer.exe" "MSIAPService.exe" "Sendevsvc.exe" "OmApSvcBroker.exe" "OneDC_Updater.exe") do (
    taskkill /F /IM "%%~P" >nul 2>&1
)

echo.
echo ============================================================================
echo [DONE] MSI Center services, UWP startup task, scheduled tasks and
echo        background processes are all disabled.
echo.
echo   * Micro Star SCM (Fn brightness / volume service) is left untouched.
echo   * MSI Scenario Lite now owns the EC exclusively; BIOS power limits
echo     (PL1/PL2) are no longer overwritten on boot.
echo   * If Settings still shows MSI Center as "On", close and reopen that page.
echo ============================================================================
pause
