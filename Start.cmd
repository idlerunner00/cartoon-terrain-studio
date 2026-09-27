@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start.ps1" %*
set "EXITCODE=%ERRORLEVEL%"
rem A double-clicked window would close at once; on a failure it stays open, so that the message can be read.
if not "%EXITCODE%"=="0" pause
exit /b %EXITCODE%
