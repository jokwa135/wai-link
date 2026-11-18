@echo off
rem Speed tweaks for the Galaxy J5 Prime. Run with "revert" to undo everything.
set ADB=%~dp0..\..\dist\lib\adb.exe
if not exist "%ADB%" set ADB=adb.exe
set APPS=com.facebook.services com.facebook.system com.facebook.appmanager com.opera.max.preinstall com.opera.max.oem com.hiya.star com.samsung.android.beaconmanager com.samsung.android.app.watchmanagerstub com.diotek.sec.lookup.dictionary

if /i "%1"=="revert" goto revert

rem Shorter animations (default 1.0)
"%ADB%" shell settings put global window_animation_scale 0.5
"%ADB%" shell settings put global transition_animation_scale 0.5
"%ADB%" shell settings put global animator_duration_scale 0.5
rem Power saving off: it slows the CPU
"%ADB%" shell settings put global low_power 0
rem Disable preinstalled background bloat (not deleted; revert re-enables)
for %%p in (%APPS%) do "%ADB%" shell pm disable-user --user 0 %%p
goto :eof

:revert
"%ADB%" shell settings put global window_animation_scale 1.0
"%ADB%" shell settings put global transition_animation_scale 1.0
"%ADB%" shell settings put global animator_duration_scale 1.0
for %%p in (%APPS%) do "%ADB%" shell pm enable %%p
