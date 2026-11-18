# Play the phone's audio on this PC over Bluetooth (PC acts as a Bluetooth speaker).
# Needs: the phone paired with this PC once in Bluetooth settings. Keep this window open while listening.
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Audio.AudioPlaybackConnection, Windows.Media.Audio, ContentType = WindowsRuntime]
$null = [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType = WindowsRuntime]

$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$type) {
    $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(-1) | Out-Null; $t.Result
}

$selector = [Windows.Media.Audio.AudioPlaybackConnection]::GetDeviceSelector()
$devices = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync($selector)) ([Windows.Devices.Enumeration.DeviceInformationCollection])
if ($devices.Count -eq 0) { Write-Host "No paired phone found. Pair the phone with this PC in Bluetooth settings first."; exit 1 }

$match = @($devices | Where-Object { $_.Name -match ($args[0], 'SM-G570|J5|Galaxy' -ne $null)[0] })
$dev = if ($match.Count) { $match[0] } else { $devices[0] }
Write-Host "Connecting audio from: $($dev.Name)"

$conn = [Windows.Media.Audio.AudioPlaybackConnection]::TryCreateFromId($dev.Id)
$conn.Start()
$res = Await ($conn.OpenAsync()) ([Windows.Media.Audio.AudioPlaybackConnectionOpenResult])
if ($res.Status -ne 'Success') { Write-Host "Failed: $($res.Status) $($res.ExtendedError)"; exit 1 }
Write-Host "Phone audio now plays on this PC. Close this window to stop."
while ($true) { Start-Sleep 5 }
