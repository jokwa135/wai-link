# Optimize-J5Prime.ps1
# Stealth hardware & OS optimization for Samsung Galaxy J5 Prime (SM-G570Y, Knox 0x0)
param(
    [string]$Serial = "",   # adb serial; empty = the first connected device
    [switch]$SkipCompile
)

$ErrorActionPreference = "Continue"
$adb = Join-Path $PSScriptRoot "..\..\dist\lib\adb.exe"
if (-not (Test-Path $adb)) { $adb = "adb.exe" }
if (-not $Serial) {
    $Serial = ((& $adb devices) -split "`r?`n" | Where-Object { $_ -match '\tdevice$' } | Select-Object -First 1) -replace '\t.*$', ''
    if (-not $Serial) { Write-Host "No Android device connected."; exit 1 }
}

function Invoke-AdbShell {
    param([string]$Cmd)
    & $adb -s $Serial shell $Cmd 2>&1
}

Write-Host "=== [1/7] Disconnecting duplicate Wi-Fi ADB transports ==="
if ($Serial -notmatch ':\d+$') { & $adb disconnect 2>&1 | Out-Null }  # drop Wi-Fi duplicates of a USB device

Write-Host "=== [2/7] Stopping background CPU/RAM hogs (TikTok, Shopee, Chrome, Play Store) ==="
Invoke-AdbShell "am force-stop com.ss.android.ugc.trill; am force-stop com.shopee.ph; am force-stop com.android.chrome; am force-stop com.android.vending"

Write-Host "=== [3/7] Uninstalling 54 Samsung High Contrast Theme packages & residual loan apps ==="
$thirdParty = (& $adb -s $Serial shell "pm list packages -u" 2>&1) -split "`r?`n" | ForEach-Object { $_ -replace '^package:', '' } | Where-Object { $_ }
$removePatterns = @(
    '^com\.samsung\.High_contrast_theme_II\.',
    '^com\.monotype\.android\.font\.',
    '^com\.peso\.',
    '^phl\.pitacash\.',
    '^com\.pesohaus\.',
    '^com\.Surity\.',
    '^s\.bene\.ph$'
)
foreach ($pkg in $thirdParty) {
    foreach ($pat in $removePatterns) {
        if ($pkg -match $pat) {
            Write-Host "  Removing $pkg ..."
            & $adb -s $Serial shell "pm uninstall $pkg" 2>&1 | Out-Null
            & $adb -s $Serial shell "pm uninstall --user 0 $pkg" 2>&1 | Out-Null
            break
        }
    }
}

Write-Host "=== [4/7] Stripping /data updates of disabled bloatware to reclaim ~1.5 GB internal flash ==="
$disabledBloatWithUpdates = @(
    "com.google.android.googlequicksearchbox",
    "com.google.android.youtube",
    "com.google.android.apps.maps",
    "com.google.android.videos",
    "com.google.android.gm",
    "com.google.android.apps.docs",
    "com.google.android.music",
    "com.sec.android.app.sbrowser",
    "com.sec.android.app.samsungapps",
    "com.samsung.android.themestore",
    "com.samsung.android.game.gamehome",
    "com.samsung.android.game.gametools",
    "com.facebook.katana",
    "com.facebook.system",
    "com.facebook.appmanager",
    "com.facebook.services",
    "com.microsoft.office.excel",
    "com.microsoft.office.word",
    "com.microsoft.office.powerpoint",
    "com.microsoft.skydrive"
)
foreach ($pkg in $disabledBloatWithUpdates) {
    & $adb -s $Serial shell "pm uninstall $pkg" 2>&1 | Out-Null
    & $adb -s $Serial shell "pm uninstall --user 0 $pkg" 2>&1 | Out-Null
    & $adb -s $Serial shell "pm disable-user --user 0 $pkg" 2>&1 | Out-Null
}

Write-Host "=== [5/7] Disabling remaining non-essential Samsung background daemons ==="
$extraDaemons = @(
    "com.samsung.android.themecenter",
    "com.samsung.android.app.powerplanning",
    "com.samsung.android.sm.provider",
    "com.samsung.android.sm.devicesecurity",
    "com.samsung.android.lool",
    "com.samsung.InputEventApp",
    "com.sec.android.provider.badge",
    "com.sec.android.daemonapp",
    "com.samsung.android.MtpApplication",
    "com.samsung.android.app.galaxyfinder",
    "com.samsung.android.rubin.app",
    "com.samsung.android.scloud",
    "com.samsung.android.SamsungPass",
    "com.samsung.android.samsungpass",
    "com.samsung.android.samsungpassautofill",
    "com.samsung.clipboardsaveservice",
    "com.sec.android.diagmonagent",
    "com.samsung.android.securitylogagent",
    "com.samsung.android.bbc.bbcagent",
    "com.samsung.android.da.daagent"
)
foreach ($d in $extraDaemons) {
    & $adb -s $Serial shell "pm disable-user --user 0 $d; am force-stop $d" 2>&1 | Out-Null
}

# Re-apply debloat-list.txt to ensure all listed packages remain disabled
$debloatFile = Join-Path $PSScriptRoot "debloat-list.txt"
if (Test-Path $debloatFile) {
    Get-Content $debloatFile | Where-Object { $_ -and $_.Trim() } | ForEach-Object {
        $p = $_.Trim()
        & $adb -s $Serial shell "pm disable-user --user 0 $p" 2>&1 | Out-Null
    }
}

Write-Host "=== [6/7] Applying Kernel / ActivityManager / Display / GPU / AppOps Stealth Tuning ==="
$tuningCmd = @"
wm size reset
wm density reset
settings put global http_proxy :0
settings delete global http_proxy
settings delete global global_http_proxy_host
settings delete global global_http_proxy_port
settings put global window_animation_scale 0.25
settings put global transition_animation_scale 0.25
settings put global animator_duration_scale 0.25
settings put global low_power 0
settings put global activity_manager_constants max_cached_processes=2,background_settle_time=5000,fg_to_bg_fgs_grace_duration=2000,content_provider_retain_time=5000,gc_timeout=2000,gc_min_interval=10000
setprop debug.hwui.renderer skiagl
logcat -G 64K
logcat -c
cmd appops set com.ss.android.ugc.trill RUN_IN_BACKGROUND ignore
cmd appops set com.shopee.ph RUN_IN_BACKGROUND ignore
cmd appops set com.android.chrome RUN_IN_BACKGROUND ignore
cmd appops set com.android.vending RUN_IN_BACKGROUND ignore
pm trim-caches 999G
sm fstrim
"@
foreach ($line in ($tuningCmd -split "`r?`n")) {
    $l = $line.Trim()
    if ($l) { Invoke-AdbShell $l | Out-Null }
}

if (-not $SkipCompile) {
    Write-Host "=== [7/7] Compiling core apps to 100% Native ARM Machine Code (speed) ==="
    $compileTargets = @(
        "com.android.systemui",
        "com.sec.android.app.launcher",
        "com.android.vending",
        "com.android.chrome",
        "com.shopee.ph",
        "com.ss.android.ugc.trill"
    )
    foreach ($t in $compileTargets) {
        Write-Host "  Compiling $t (-m speed)..."
        Invoke-AdbShell "cmd package compile -m speed -f $t" | Out-Null
        Invoke-AdbShell "cmd package compile -m speed -f --secondary-dex $t" | Out-Null
    }
    Invoke-AdbShell "pm trim-caches 999G; sm fstrim" | Out-Null
}

Write-Host "=== Optimization Complete ==="
