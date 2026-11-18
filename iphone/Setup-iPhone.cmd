@echo off
title WAI Link - iPhone setup
rem One-time iPhone setup. Connect the iPhone with a USB cable, unlock it and tap "Trust".
rem 1. Shows the Developer Mode switch on the iPhone (Settings > Privacy & Security > Developer Mode).
rem 2. Opens Sideloadly to install WebDriverAgentRunner.ipa, signed with your own Apple ID.
rem After that, open WAI Link and pick iPhone. The first connection should be over USB;
rem later ones work over Wi-Fi.

set "HERE=%~dp0"
set "PY=%HERE%python\python.exe"
rem Keep the iPhone pairing records in WAI Link's data folder (same choice as the app:
rem next to WAI Link.exe, or %LOCALAPPDATA%\WAI Link\data if that folder is read-only).
set "DATA=%HERE%..\data"
if not exist "%DATA%" mkdir "%DATA%" 2>nul
(echo.>"%DATA%\.write-test") 2>nul && (del "%DATA%\.write-test") || (set "DATA=%LOCALAPPDATA%\WAI Link\data")
set "USERPROFILE=%DATA%\iphone"
if not exist "%USERPROFILE%" mkdir "%USERPROFILE%"

if not exist "%PY%" (
  echo The bundled Python is missing: %PY%
  echo Download the full WAI Link release zip, or run scripts\build.ps1 from the source.
  pause
  exit /b 1
)

net start "Apple Mobile Device Service" >nul 2>&1
echo Showing the Developer Mode switch on the iPhone...
"%PY%" -I -m pymobiledevice3 amfi reveal-developer-mode

set "SIDELOADLY="
for %%P in ("%ProgramFiles%\Sideloadly\sideloadly.exe" "%ProgramFiles(x86)%\Sideloadly\sideloadly.exe" "%LOCALAPPDATA%\Sideloadly\sideloadly.exe") do (
  if exist %%P set "SIDELOADLY=%%~P"
)
if not defined SIDELOADLY (
  echo.
  echo Sideloadly was not found. Install it from https://sideloadly.io, then run this again,
  echo or drag "%HERE%WebDriverAgentRunner.ipa" into Sideloadly yourself.
  start "" https://sideloadly.io
  pause
  exit /b 0
)
echo Opening Sideloadly with WebDriverAgent. Sign in with your Apple ID and press Start.
start "" "%SIDELOADLY%" "%HERE%WebDriverAgentRunner.ipa"
