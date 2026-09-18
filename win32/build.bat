@echo off
REM 编译 win32 版。需要系统自带 .NET Framework 4.x 的 csc.exe。
REM 产物在 build\ 目录。不依赖任何第三方 DLL。
setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo 找不到 csc.exe，需要 .NET Framework 4.x
    pause
    exit /b 1
)

if not exist "build" mkdir build

echo 编译中……
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:build\WeiboDelete.exe ^
  /reference:System.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  src\*.cs

if errorlevel 1 (
    echo 编译失败
    pause
    exit /b 1
)

echo.
echo 编译完成，产物在 build\
echo.
echo 运行前请确认 browser\chrome-win64\chrome.exe 存在。
echo 双击 build\WeiboDelete.exe 运行。
pause
