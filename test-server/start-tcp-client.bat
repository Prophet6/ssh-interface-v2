@echo off
title SSH Mock - TCP Client (built-in TCP/IP Server)
cd /d "%~dp0"

where python >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python was not found in PATH.
    pause
    exit /b 1
)

set /p HOST="RMC4 / processor IP: "
if "%HOST%"=="" (
    echo No IP entered.
    pause
    exit /b 1
)

set /p PORT="Port [5000]: "
if "%PORT%"=="" set PORT=5000

echo.
echo Connecting TCP to %HOST%:%PORT%
echo Enable the SIMPL Windows TCP/IP Server symbol first.
echo Type in this window to send to the processor. [RX] is data from it.
echo Ctrl+C to stop.
echo.

python -u mock.py tcp-client --host %HOST% --port %PORT%

if errorlevel 1 (
    echo.
    echo Client exited with an error.
    pause
)
