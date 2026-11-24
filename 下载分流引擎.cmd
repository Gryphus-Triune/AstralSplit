@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Engine.ps1"
set "engine_result=%ERRORLEVEL%"
if not "%engine_result%"=="0" echo Engine setup failed. See the portable package guide.
pause
exit /b %engine_result%
