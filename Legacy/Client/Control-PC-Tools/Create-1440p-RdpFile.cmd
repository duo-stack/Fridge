@echo off
setlocal
title Create 2560x1440 RDP File

echo ========================================
echo   Create 2560x1440 RDP File
echo ========================================
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Create-1440p-RdpFile.ps1"
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
  echo Result: SUCCESS
) else (
  echo Result: FAILED ^(exit code %RESULT%^)
)
echo.
echo Press any key to close this window...
pause >nul
exit /b %RESULT%
