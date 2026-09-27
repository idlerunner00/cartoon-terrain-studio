@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start.ps1" %*
exit /b %ERRORLEVEL%
