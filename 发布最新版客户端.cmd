@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-client.ps1"
set "publishExitCode=%ERRORLEVEL%"
if not "%publishExitCode%"=="0" echo Publish failed. See the message above.
pause
exit /b %publishExitCode%
