@echo off
title SSH Mock - SSH Server (Crestron Client test)
cd /d "%~dp0"

where python >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python was not found in PATH.
    pause
    exit /b 1
)

echo.
echo SSH mock SERVER - for the Crestron SSH Interface Client module
echo Listen: 0.0.0.0:2222   user/pass: crestron / crestron
echo Point the Client module IP_Address$ at THIS PC, IP_Port 2222.
echo Type in this window to send to the processor. [RX] is data from it.
echo Ctrl+C to stop.
echo.

python -u mock.py ssh-server --port 2222 --user crestron --password crestron

if errorlevel 1 (
    echo.
    echo Server exited with an error.
    pause
)
