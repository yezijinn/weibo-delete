@echo off
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
    echo.
    echo   Not installed yet. Running install.bat ...
    echo.
    call install.bat
    if not exist ".venv\Scripts\python.exe" (
        echo   Install failed. Please run install.bat manually.
        pause
        exit /b 1
    )
)

call ".venv\Scripts\activate.bat"
python weibo_delete.py --menu
