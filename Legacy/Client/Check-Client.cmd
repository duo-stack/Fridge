@echo off
setlocal
title FRP RDP Client Check

fltmc.exe >nul 2>&1
if errorlevel 1 (
  echo Requesting administrator permission...
  powershell.exe -NoLogo -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo ========================================
echo   FRP RDP Client Check
echo ========================================
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Check-Client.ps1"
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
  echo Result: SUCCESS
) else (
  echo Result: CHECK FAILED ^(exit code %RESULT%^)
)
echo.
echo Press any key to close this window...
pause >nul
exit /b %RESULT%
