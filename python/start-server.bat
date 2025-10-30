@echo off
REM YellowFox CamouFox Server Launcher
REM Activates virtual environment and runs camoufox-server.py with all arguments

REM Get the directory where this script is located
set SCRIPT_DIR=%~dp0
echo Current dir: %SCRIPT_DIR%

set ACTIVATE_SCRIPT=%SCRIPT_DIR%venv\Scripts\activate.bat
echo Activate VENV script: %ACTIVATE_SCRIPT%

REM Activate virtual environment if it exists
if exist "%ACTIVATE_SCRIPT%" (
    echo "Venv activate script found! Activating..."
    call "%ACTIVATE_SCRIPT%"
    echo "Activated venv."
) ELSE (
    echo "Venv activate script NOT FOUND!"
)


REM Run the Python server with all passed arguments
python "%SCRIPT_DIR%camoufox-server.py" %*
