@echo off
title Rotate SSH mock host key
cd /d "%~dp0"
python mock.py ssh-server --rotate-key
echo.
echo Next start-ssh-server.bat will generate a new fingerprint.
echo Use that to test Accept_New_Key on the Crestron client.
pause
