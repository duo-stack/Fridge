@echo off
setlocal
title FRP RDP Client Setup

if /I not "%~1"=="elevated" (
  echo ========================================
  echo   FRP RDP Client Setup Notice
  echo ========================================
  echo.
  echo Windows Defender may flag the official frpc.exe as a
  echo potentially unwanted application because it is a tunneling tool.
  echo This can be a false positive. Confirm the package source, then
  echo allow or restore frpc.exe if Defender blocks it.
  echo.
  choice.exe /C YN /N /M "Continue? [Y/N]: "
  if errorlevel 2 exit /b 2
  echo.
)

fltmc.exe >nul 2>&1
if errorlevel 1 (
  echo Requesting administrator permission...
  powershell.exe -NoLogo -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList 'elevated' -Verb RunAs"
  exit /b
)

echo ========================================
echo   FRP RDP Client Setup
echo ========================================
echo FRP version is fixed at 0.65.0.
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Client.ps1"
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
