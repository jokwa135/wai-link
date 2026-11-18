<#
.SYNOPSIS
  Builds WAI Link into dist\ and, with -Zip, a release zip in release\.

.DESCRIPTION
  dist\ is the complete portable app:
    WAI Link.exe        single-file, self-contained .NET app (no .NET install needed)
    lib\                Android renderer (scrcpy) and adb
    iphone\python\      bundled Python with pymobiledevice3 (+ WAI Link's patch)
    iphone\bridge\      WAI Link's iPhone bridge scripts
    data\               created at run time: settings, logs, iPhone pairing, shared files
  Existing lib\, iphone\python\ and data\ are kept; use -Refresh to rebuild lib\ and iphone\python\.
  Everything the build downloads or caches stays in .build\ inside the repository.

  Needs: .NET SDK 10, uv (https://docs.astral.sh/uv/) and git on PATH, or set $env:DOTNET / $env:UV.
#>
param(
    [switch]$Zip,
    [switch]$Refresh,
    [string]$Version = "2.0.0"
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root   = Split-Path $PSScriptRoot -Parent
$dist   = Join-Path $root "dist"
$build  = Join-Path $root ".build"
$dotnet = if ($env:DOTNET) { $env:DOTNET } else { "dotnet" }
$uv     = if ($env:UV) { $env:UV } else { "uv" }
$scrcpyVersion = "5.0.1"
$pythonVersion = "3.12"

New-Item -ItemType Directory -Force $dist, $build | Out-Null
if (-not $env:UV_CACHE_DIR) { $env:UV_CACHE_DIR = Join-Path $build "uv-cache" }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $build "nuget" }
$env:UV_PYTHON_INSTALL_DIR = Join-Path $build "python-install"

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }
# Runs an external tool. Windows PowerShell 5 turns a tool's progress text on stderr into errors,
# so only the exit code decides success.
function Run([string]$what, [scriptblock]$command) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $command 2>&1 | ForEach-Object { Write-Host "   $_" } }
    finally { $ErrorActionPreference = $saved }
    Check $what
}

# 1. The app: one self-contained exe, published to a staging folder and swapped into dist\.
Step "Publishing WAI Link"
$publish = Join-Path $build "publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Run "dotnet publish" {
    & $dotnet publish (Join-Path $root "PhoneBridge\PhoneBridge.csproj") -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -p:Version=$Version -o $publish --nologo -v q
}
# Old app binaries in dist\ (from earlier, multi-file builds) are replaced; lib, iphone and data stay.
Get-ChildItem $dist -File | Where-Object { $_.Extension -in ".exe", ".dll", ".json", ".pdb" } | Remove-Item -Force
Copy-Item (Join-Path $publish "*") $dist -Recurse -Force

# 2. Android renderer: the official scrcpy release, renamed so it is clearly WAI Link's copy.
$lib = Join-Path $dist "lib"
if ($Refresh -or -not (Test-Path (Join-Path $lib "wailink-render.exe"))) {
    Step "Downloading scrcpy $scrcpyVersion"
    $zipFile = Join-Path $build "scrcpy-$scrcpyVersion.zip"
    if (-not (Test-Path $zipFile)) {
        Invoke-WebRequest -UseBasicParsing "https://github.com/Genymobile/scrcpy/releases/download/v$scrcpyVersion/scrcpy-win64-v$scrcpyVersion.zip" -OutFile $zipFile
    }
    $unpacked = Join-Path $build "scrcpy"
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    Expand-Archive $zipFile $unpacked
    $src = Get-ChildItem $unpacked -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force $lib | Out-Null
    foreach ($name in "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "SDL3.dll", "libusb-1.0.dll", "scrcpy-server") {
        Copy-Item (Join-Path $src.FullName $name) $lib -Force
    }
    Get-ChildItem $src.FullName -Filter "*.dll" | Where-Object { $_.Name -match '^(avcodec|avformat|avutil|swresample)' } | Copy-Item -Destination $lib -Force
    Copy-Item (Join-Path $src.FullName "scrcpy.exe") (Join-Path $lib "wailink-render.exe") -Force
    Copy-Item (Join-Path $src.FullName "scrcpy-server") (Join-Path $lib "wailink-server") -Force
}

# 3. iPhone: a private, relocatable Python with pinned packages and WAI Link's pymobiledevice3 patch.
$python = Join-Path $dist "iphone\python"
if ($Refresh -or -not (Test-Path (Join-Path $python "python.exe"))) {
    Step "Preparing Python $pythonVersion for the iPhone side"
    Run "uv python install" { & $uv python install $pythonVersion --no-bin }
    $installed = Get-ChildItem $env:UV_PYTHON_INSTALL_DIR -Directory | Where-Object { $_.Name -like "cpython-$pythonVersion*-windows-x86_64-none" } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $installed) { throw "uv did not install Python $pythonVersion" }
    if (Test-Path $python) { Remove-Item $python -Recurse -Force }
    Copy-Item $installed.FullName $python -Recurse
    Remove-Item (Join-Path $python "Lib\EXTERNALLY-MANAGED") -ErrorAction SilentlyContinue

    Step "Installing iPhone packages"
    # --no-cache and copies: the patch below edits installed files and must never touch a shared cache.
    Run "uv pip install" {
        & $uv pip install --python (Join-Path $python "python.exe") --no-cache --link-mode copy -r (Join-Path $root "iphone\requirements.txt")
    }

    Step "Applying the pymobiledevice3 patch"
    $site = Join-Path $python "Lib\site-packages"
    $patch = Join-Path $root "iphone\patches\pymobiledevice3-11.50.3.patch"
    # Outside any repository (GIT_DIR points nowhere), so the paths are relative to site-packages.
    $env:GIT_DIR = Join-Path $build "no-repo"
    Push-Location $site
    try { Run "git apply" { & git -c core.autocrlf=false apply -p1 --whitespace=nowarn $patch } }
    finally { Pop-Location; Remove-Item Env:GIT_DIR }
}

# 4. Release zip: the whole app folder without the user's data.
if ($Zip) {
    Step "Creating release zip"
    $out = Join-Path $root "release"
    New-Item -ItemType Directory -Force $out | Out-Null
    $zipPath = Join-Path $out "WAILink-$Version-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $stage = Join-Path $build "zip\WAI Link"
    if (Test-Path (Split-Path $stage)) { Remove-Item (Split-Path $stage) -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Get-ChildItem $dist -Force | Where-Object { $_.Name -ne "data" } | Copy-Item -Destination $stage -Recurse -Force
    foreach ($doc in "README.md", "LICENSE", "THIRD_PARTY_NOTICES.md") {
        if (Test-Path (Join-Path $root $doc)) { Copy-Item (Join-Path $root $doc) $stage }
    }
    Compress-Archive -Path $stage -DestinationPath $zipPath -CompressionLevel Optimal  # zip root: "WAI Link\"
    Write-Host "Release: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB)) MB)"
}
Step "Done: $dist"
