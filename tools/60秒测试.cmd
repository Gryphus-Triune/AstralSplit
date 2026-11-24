@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0SessionWatchdog.ps1"
set "test_result=%ERRORLEVEL%"
pause
exit /b %test_result%
