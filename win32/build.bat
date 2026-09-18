@echo off
REM 编译 win32 版。需要系统里有 .NET Framework 4.x 自带的 csc.exe。
REM 产物在 build\ 目录。

setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo 找不到 csc.exe，需要 .NET Framework 4.x
    pause
    exit /b 1
)

if not exist "libs\Microsoft.Web.WebView2.Core.dll" (
    echo 缺少 libs\ 里的 WebView2 DLL。
    echo 这些需要从 NuGet 包 Microsoft.Web.WebView2 里提取：
    echo   lib\net462\Microsoft.Web.WebView2.Core.dll
    echo   lib\net462\Microsoft.Web.WebView2.WinForms.dll
    echo   build\native\x64\WebView2Loader.dll
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
  /reference:libs\Microsoft.Web.WebView2.Core.dll ^
  /reference:libs\Microsoft.Web.WebView2.WinForms.dll ^
  src\*.cs

if errorlevel 1 (
    echo 编译失败
    pause
    exit /b 1
)

copy /Y libs\Microsoft.Web.WebView2.Core.dll build\ >nul
copy /Y libs\Microsoft.Web.WebView2.WinForms.dll build\ >nul
copy /Y libs\WebView2Loader.dll build\ >nul

echo.
echo 编译完成，产物在 build\
echo 双击 build\WeiboDelete.exe 运行。
pause
