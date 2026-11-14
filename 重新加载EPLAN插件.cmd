@echo off
chcp 65001 >nul
title 重新加载 EPLAN 插件 (5002)

echo ============================================================
echo  EPLAN 插件重新加载工具
echo ------------------------------------------------------------
echo  用途：EPLAN 每次重启后，插件（端口 5002）不会自动加载。
echo        双击本文件即可把插件重新注入正在运行的 EPLAN。
echo  前提：EPLAN 必须已经打开。
echo ============================================================
echo.

REM ★ 必须带 ScriptFile 参数。不带参数时 ExecuteScript 拿不到脚本路径，
REM   返回 Succeed=False 且什么都不做（旧版脚本就是这么静默失败的）。
"D:\默认存储空间\EPLAN\EplanRemote\bin\Release\EplanRemote.exe" ExecuteScript ScriptFile "C:\Temp\EplanScripts\LoadAddIn.cs"

echo.
echo ------------------------------------------------------------
echo  加载日志（应含 dllLoaded / OnInit=True）：
type "C:\Temp\addin_load_result.txt"
echo.
echo  探测 5002 端口...
netstat -ano | findstr ":5002" | findstr "LISTENING"
if errorlevel 1 (
  echo  [失败] 5002 未监听。检查 EPLAN 是否已完全启动，再试一次。
) else (
  echo  [成功] 5002 已在监听，插件就绪。
)
echo ------------------------------------------------------------
pause
