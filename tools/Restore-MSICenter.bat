@echo off
chcp 65001 >nul
:: 自动请求管理员权限
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo 正在请求管理员权限以恢复原版 MSI Center 后台服务与自启动...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================================
echo   MSI Center 后台服务与开机自启 —— 一键恢复默认工具
echo ============================================================================
echo.

echo [1/3] 正在恢复原版 MSI Center 关联的 3 个系统服务为自动启动并启动服务...
for %%S in ("MSI_Center_Service" "MSI Foundation Service" "MSI Sendevsvc") do (
    echo   - 恢复并启动服务: %%~S
    sc.exe config "%%~S" start= auto >nul 2>&1
    sc.exe start "%%~S" >nul 2>&1
)

echo.
echo [2/3] 正在恢复原版 MSI Center 关联的 2 个计划任务...
for %%T in ("\OmApSvcBroker" "\OneDC_Updater") do (
    echo   - 启用计划任务: %%~T
    schtasks /Change /TN "%%~T" /Enable >nul 2>&1
)
schtasks /Run /TN "\OmApSvcBroker" >nul 2>&1

echo.
echo [3/3] 正在恢复 MSI Center 的 UWP 启动项...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MSICenter-Startup.ps1" -Enable

echo.
echo ============================================================================
echo [完成] 原版 MSI Center 后台服务、UWP 启动项与自启动已全部恢复如初！
echo ============================================================================
pause
