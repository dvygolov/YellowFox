@echo off
REM YellowFox CamouFox Server Launcher
REM Activates virtual environment and runs camoufox-server.py with all arguments

REM Get the directory where this script is located
set SCRIPT_DIR=%~dp0

set ACTIVATE_SCRIPT=%SCRIPT_DIR%venv\Scripts\activate.bat

REM Activate virtual environment if it exists
if exist "%ACTIVATE_SCRIPT%" (
    call "%ACTIVATE_SCRIPT%"
)

REM Run the Python server with all passed arguments
python "%SCRIPT_DIR%camoufox-server.py" %*
