@echo off
rem Disables (does not delete) every app in debloat-list.txt. Run with "revert" to re-enable them all.
set ADB=%~dp0..\..\dist\lib\adb.exe
if not exist "%ADB%" set ADB=adb.exe
set LIST=%~dp0debloat-list.txt

if /i "%1"=="revert" (
  for /f "usebackq delims=" %%p in ("%LIST%") do "%ADB%" shell pm enable %%p
) else (
  for /f "usebackq delims=" %%p in ("%LIST%") do "%ADB%" shell pm disable-user --user 0 %%p
)
