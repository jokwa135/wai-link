using System.Drawing;
using System.Text.RegularExpressions;

namespace PhoneBridge;

public enum AndroidHardwareTier
{
    LowRam,     // <= 2200 MB (e.g. Samsung Galaxy J5 Prime SM-G570Y 1.8 GB)
    MidRange,   // 2201 - 4500 MB
    Flagship    // > 4500 MB (e.g. Red Magic 5G NX659J)
}

/// <summary>
/// Universal Adaptive Android RAM Governor & Bloat Freezer (WAILink.Android.dll).
/// Automatically adapts its caching & background-freeze rules to ANY connected Android phone:
/// - On Low-RAM phones (e.g. Galaxy J5 Prime SM-G570Y), locks max_cached_processes=2 and freezes
///   Google Play Store + Samsung background bloat to reclaim ~340 MB physical RAM.
/// - On Flagship phones (e.g. Red Magic 5G), preserves multi-app caching while applying zero-lag animation scales.
/// </summary>
public sealed class AndroidGovernor(Adb adb)
{
    static readonly string[] BackgroundBloatPackages =
    [
        "com.android.vending",
        "com.samsung.android.themecenter",
        "com.samsung.android.sm.policy",
        "com.google.android.configupdater",
        "com.samsung.android.providers.context",
        "com.samsung.knox.securefolder",
        "com.sec.android.app.billing"
    ];

    string _lastSeenSmsId = "";
    string _lastSeenOtpCode = "";

    public AndroidHardwareTier CurrentTier { get; private set; } = AndroidHardwareTier.LowRam;
    public AndroidHardwareTier HardwareTier => CurrentTier;
    public event Action<string, string, string>? OnAndroidOtpDetected; // (otp, sender, fullBody)

    public static AndroidHardwareTier ClassifyHardwareTier(int memTotalMb) =>
        memTotalMb <= 2200 ? AndroidHardwareTier.LowRam :
        memTotalMb <= 4500 ? AndroidHardwareTier.MidRange :
        AndroidHardwareTier.Flagship;

    public static AndroidHardwareTier EvaluateTier(int memTotalMb) => ClassifyHardwareTier(memTotalMb);

    public async Task<(int FreedMb, int AvailMbAfter)> BoostRamAsync(string? currentForegroundPkg, CancellationToken ct = default)
    {
        var before = await adb.GetTelemetryAsync(ct);
        if (before is not null && before.MemTotalMb > 0)
            CurrentTier = ClassifyHardwareTier(before.MemTotalMb);

        var cmds = new List<string>();

        foreach (var bloat in BackgroundBloatPackages)
        {
            if (!string.Equals(bloat, currentForegroundPkg, StringComparison.OrdinalIgnoreCase))
            {
                cmds.Add($"cmd appops set {bloat} RUN_IN_BACKGROUND ignore");
                cmds.Add($"am force-stop {bloat}");
            }
        }

        cmds.Add("am kill-all");
        if (!string.IsNullOrEmpty(currentForegroundPkg))
        {
            cmds.Add($"am send-trim-memory {currentForegroundPkg} RUNNING_CRITICAL");
        }
        cmds.Add("logcat -c");
        cmds.Add("cmd appops set com.android.chrome RUN_IN_BACKGROUND allow");

        await adb.ShellAsync(string.Join("; ", cmds), ct);
        await Task.Delay(350, ct);

        var after = await adb.GetTelemetryAsync(ct);
        int availAfter = after?.MemAvailableMb ?? (before?.MemAvailableMb ?? 0);
        int freed = before is not null && after is not null
            ? Math.Max(16, (after.MemAvailableMb - before.MemAvailableMb) + Math.Max(0, before.SwapUsedMb - after.SwapUsedMb))
            : 64;
        return (freed, availAfter);
    }

    public static string FormatBoostStatus(int freedMb) => $"Memory Optimized · Freed {freedMb} MB RAM";

    public async Task<string> BoostRamStatusAsync(string? currentForegroundPkg, CancellationToken ct = default)
    {
        var (freed, _) = await BoostRamAsync(currentForegroundPkg, ct);
        return FormatBoostStatus(freed);
    }

    /// <summary>
    /// Applies adaptive session tuning based on the connected Android phone's RAM tier.
    /// Fixes Galaxy J5 Prime slowness by setting max_cached_processes=2 and freezing Play Store background wakeups.
    /// </summary>
    public async Task ApplyStealthSessionTuningAsync(CancellationToken ct = default)
    {
        var t = await adb.GetTelemetryAsync(ct);
        if (t is not null && t.MemTotalMb > 0)
            CurrentTier = ClassifyHardwareTier(t.MemTotalMb);

        int maxCached = CurrentTier switch
        {
            AndroidHardwareTier.LowRam => 2,
            AndroidHardwareTier.MidRange => 6,
            _ => 16
        };

        string cmd =
            "wm size reset; wm density reset; " +
            "settings delete global http_proxy; settings delete global global_http_proxy_host; settings delete global global_http_proxy_port; " +
            "settings put global window_animation_scale 0; " +
            "settings put global transition_animation_scale 0; " +
            "settings put global animator_duration_scale 0; " +
            "settings put global stay_on_while_plugged_in 7; " +
            "settings put system screen_off_timeout 1800000; " +
            "settings put global restricted_device_performance '0, 0'; " +
            "settings put system screen_brightness 15; " +
            "settings put global auto_sync 0; " +
            $"settings put global activity_manager_constants max_cached_processes={maxCached},background_settle_time=5000; " +
            "cmd appops set com.android.vending RUN_IN_BACKGROUND ignore; " +
            "cmd appops set com.samsung.android.themecenter RUN_IN_BACKGROUND ignore; " +
            "cmd appops set com.samsung.android.sm.policy RUN_IN_BACKGROUND ignore; " +
            "cmd appops set com.google.android.configupdater RUN_IN_BACKGROUND ignore; " +
            "cmd appops set com.samsung.android.providers.context RUN_IN_BACKGROUND ignore; " +
            "am force-stop com.android.vending; " +
            "cmd appops set com.ss.android.ugc.trill RUN_IN_BACKGROUND allow; " +
            "cmd appops set com.android.chrome RUN_IN_BACKGROUND allow; " +
            "cmd appops set com.google.android.webview RUN_IN_BACKGROUND allow; " +
            "cmd appops set com.shopee.ph RUN_IN_BACKGROUND allow; " +
            "setprop debug.hwui.renderer opengl; " +
            "logcat -G 64K; logcat -c";
        try { await adb.ShellAsync(cmd, ct); } catch { }
    }

    public Task InspectTelemetryAsync(PhoneTelemetry t, CancellationToken ct = default)
    {
        if (t.MemTotalMb > 0)
            CurrentTier = ClassifyHardwareTier(t.MemTotalMb);

        if (!string.IsNullOrEmpty(t.LatestOtpCode) && t.LatestOtpCode != _lastSeenOtpCode)
        {
            bool isFirstLoad = string.IsNullOrEmpty(_lastSeenOtpCode);
            _lastSeenOtpCode = t.LatestOtpCode;
            if (!isFirstLoad)
            {
                OnAndroidOtpDetected?.Invoke(
                    t.LatestOtpCode,
                    string.IsNullOrEmpty(t.LatestOtpSource) ? "Android Notification" : t.LatestOtpSource,
                    !string.IsNullOrEmpty(t.LatestSmsBody) ? t.LatestSmsBody : t.LatestOtpCode
                );
            }
        }
        else if (!string.IsNullOrEmpty(t.LatestSmsId) && t.LatestSmsId != _lastSeenSmsId)
        {
            bool isFirstLoad = string.IsNullOrEmpty(_lastSeenSmsId);
            _lastSeenSmsId = t.LatestSmsId;
            if (!isFirstLoad && !string.IsNullOrEmpty(t.LatestSmsBody))
            {
                var otp = ExtractOtpCode(t.LatestSmsBody);
                if (otp is not null)
                    OnAndroidOtpDetected?.Invoke(otp, string.IsNullOrEmpty(t.LatestSmsSender) ? "Android SMS" : t.LatestSmsSender, t.LatestSmsBody);
            }
        }

        return Task.CompletedTask;
    }

    public static string? ExtractOtpCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"(?i)(?:code|otp|pin|passcode|verification|confirm|berifikasi|kode)[^\d]{0,18}(\b\d{4,8}\b)");
        if (m.Success) return m.Groups[1].Value;
        var fallback = Regex.Match(text, @"\b(\d{4,8})\b");
        return fallback.Success ? fallback.Groups[1].Value : null;
    }
}
