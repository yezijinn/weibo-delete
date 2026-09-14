@echo off
cd /d "%~dp0"

echo.
echo ================================================
echo   weibo-delete - install
echo ================================================
echo.

echo   [1/3] Checking Python ...
where python >nul 2>nul
if errorlevel 1 (
    echo.
    echo   [ERROR] Python not found.
    echo.
    echo   Please install Python first:
    echo     1. Open https://www.python.org/downloads/
    echo     2. Download and run the installer
    echo     3. IMPORTANT: tick "Add Python to PATH"
    echo     4. Then run install.bat again
    echo.
    pause
    exit /b 1
)
echo         OK

echo.
echo   [2/3] Creating environment ^(about 1 min^) ...
python -m venv .venv
if errorlevel 1 (
    echo   [ERROR] failed to create .venv
    pause
    exit /b 1
)
echo         OK

echo.
echo   [3/3] Downloading dependencies ^(about 150 MB^) ...
echo         Please wait, do not close this window.
call ".venv\Scripts\activate.bat"
python -m pip install --upgrade pip -q
python -m pip install -r requirements.txt
if errorlevel 1 (
    echo.
    echo   [ERROR] failed to install dependencies. Check your network.
    pause
    exit /b 1
)
python -m playwright install chromium
if errorlevel 1 (
    echo.
    echo   [ERROR] failed to download browser. Check your network.
    pause
    exit /b 1
)

echo.
echo ================================================
echo   Install done.
echo   From now on, just double-click run.bat
echo ================================================
echo.
pause
