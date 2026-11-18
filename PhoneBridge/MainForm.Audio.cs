using System.Diagnostics;
using System.Text;

namespace PhoneBridge;

// iPhone sound on the PC. While the iPhone view is open (and the setting is on), the PC acts as a
// Bluetooth speaker for the paired iPhone through Windows' AudioPlaybackConnection. The connection
// lives in a hidden PowerShell child process, so it stops with WAI Link.
public sealed partial class MainForm
{
    Process? _audioProcess;

    const string AudioScript = """
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Media.Audio.AudioPlaybackConnection, Windows.Media.Audio, ContentType = WindowsRuntime]
        $null = [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType = WindowsRuntime]
        $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
        function Await($op, [Type]$type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(-1) | Out-Null; $t.Result }
        $selector = [Windows.Media.Audio.AudioPlaybackConnection]::GetDeviceSelector()
        $devices = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync($selector)) ([Windows.Devices.Enumeration.DeviceInformationCollection])
        $dev = @($devices | Where-Object { $_.Name -match 'iPhone' })[0]
        if (-not $dev) { exit 2 }
        $conn = [Windows.Media.Audio.AudioPlaybackConnection]::TryCreateFromId($dev.Id)
        $conn.Start()
        $res = Await ($conn.OpenAsync()) ([Windows.Media.Audio.AudioPlaybackConnectionOpenResult])
        if ($res.Status -ne 'Success') { exit 3 }
        while ($true) { Start-Sleep 5 }
        """;

    PhoneSoundPlayer? _mirrorSound;

    // Called on view changes, setting changes and the telemetry tick (which also retries a dropped connection).
    void UpdatePhoneAudio()
    {
        bool want = !_closing && _viewMode == StudioViewMode.IphoneOnly && ShellStyle.PhoneSound;
        // iOS 27+: the phone's own sound arrives with the screen stream (in sync, no Bluetooth needed).
        if (want && (_mirrorSound is not null || PhoneSoundPlayer.Available()))
        {
            StopPhoneAudio();
            _mirrorSound ??= new PhoneSoundPlayer();
            return;
        }
        _mirrorSound?.Dispose();
        _mirrorSound = null;
        bool running = _audioProcess is { HasExited: false };
        if (want && !running && DateTime.UtcNow >= _audioRetryAt)
        {
            // A dropped or refused connection is retried at most every 30 s.
            _audioRetryAt = DateTime.UtcNow.AddSeconds(30);
            StartPhoneAudio();
        }
        else if (!want && running) { StopPhoneAudio(); _audioRetryAt = DateTime.MinValue; }
    }

    DateTime _audioRetryAt = DateTime.MinValue;

    void StartPhoneAudio()
    {
        StopPhoneAudio();
        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(AudioScript));
            _audioProcess = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            IphoneController.AssignToChildJob(_audioProcess);
        }
        catch { _audioProcess = null; }
    }

    void StopPhoneAudio()
    {
        var process = _audioProcess;
        _audioProcess = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch { }
        process.Dispose();
    }
}
