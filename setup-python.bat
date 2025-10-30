@echo off
echo Setting up Python environment for YellowFox...
echo.

REM Create virtual environment in python folder
cd python
if not exist venv (
    echo Creating virtual environment...
    python -m venv venv
    if errorlevel 1 (
        echo Error: Failed to create virtual environment
        echo Make sure Python is installed and in PATH
        pause
        exit /b 1
    )
)

REM Activate and install dependencies
echo Installing dependencies...
call venv\Scripts\activate.bat
python -m pip install --upgrade pip
pip install -r requirements.txt

if errorlevel 1 (
    echo Error: Failed to install dependencies
    pause
    exit /b 1
)

echo.
echo ========================================
echo Setup complete!
echo ========================================
echo.
echo Virtual environment created at: python\venv
echo.
echo To activate manually:
echo   cd python
echo   venv\Scripts\activate
echo.
pause
