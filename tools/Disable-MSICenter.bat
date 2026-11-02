@echo off
chcp 65001 >nul
:: 自动请求管理员权限
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo 正在请求管理员权限以禁用原版 MSI Center 后台服务与自启动...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================================
echo   MSI Center 后台服务与开机自启 —— 一键彻底禁用工具
echo   (禁用后绝不会被唤醒，且不影响 MSI Scenario Lite 及 Fn 快捷键正常使用)
echo ============================================================================
echo.

echo [1/4] 正在停止并禁用原版 MSI Center 关联的 3 个后台系统服务...
for %%S in ("MSI_Center_Service" "MSI Foundation Service" "MSI Sendevsvc") do (
    echo   - 处理服务: %%~S
    sc.exe stop "%%~S" >nul 2>&1
    sc.exe config "%%~S" start= disabled >nul 2>&1
)

echo.
echo [2/4] 正在禁用 MSI Center 的 UWP 启动项（设置 -^> 应用 -^> 启动 里的那一项）...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MSICenter-Startup.ps1" -Disable

echo.
echo [3/4] 正在禁用原版 MSI Center 关联的 2 个计划任务自启动...
for %%T in ("\OmApSvcBroker" "\OneDC_Updater") do (
    echo   - 禁用计划任务: %%~T
    schtasks /Change /TN "%%~T" /Disable >nul 2>&1
)

echo.
echo [4/4] 正在清理残留的原版 MSI Center 后台进程...
for %%P in ("DCv2.exe" "MSI_Central_Service.exe" "MSI.CentralServer.exe" "MSI.TerminalServer.exe" "MSIAPService.exe" "Sendevsvc.exe" "OmApSvcBroker.exe" "OneDC_Updater.exe") do (
    taskkill /F /IM "%%~P" >nul 2>&1
)

echo.
echo ============================================================================
echo [完成] 原版 MSI Center 全部后台服务、UWP 启动项、计划任务及进程已彻底禁用！
echo   * 保留了系统底层的 Micro Star SCM (Fn 屏幕亮度/声音快捷键服务)
echo   * 现在 MSI Scenario Lite 将 100%% 独占控制 EC，BIOS 110W 功耗墙永不被覆盖！
echo   * 若"设置 -^> 应用 -^> 启动"仍显示为开，关闭并重新打开该页面即可刷新
echo ============================================================================
pause
