using System.Diagnostics;

namespace PhoneBridge;

/// <summary>
/// Every file WAI Link uses lives next to WAI Link.exe, so the folder can be moved or zipped as a unit:
/// lib\ (Android renderer and adb), iphone\ (Python runtime and bridge scripts) and data\ (everything
/// the app writes: settings, logs, iPhone pairing, shared files).
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = AppContext.BaseDirectory;
    public static string Lib => Path.Combine(Root, "lib");
    public static string Iphone => Path.Combine(Root, "iphone");
    public static string Python => Path.Combine(Iphone, "python", "python.exe");
    public static string IphoneBridge => Path.Combine(Iphone, "bridge");

    /// <summary>data\ next to the exe; if that folder is read-only (e.g. under Program Files),
    /// %LOCALAPPDATA%\WAI Link\data instead.</summary>
    public static string Data { get; } = ChooseDataFolder();
    public static string Logs => Folder(Data, "logs");
    public static string Shared => Folder(Data, "shared");
    public static string Downloads => Folder(Data, "downloads");
    public static string Snaps => Folder(Data, "snaps");
    /// <summary>Home folder for the iPhone tools; pymobiledevice3 keeps its pairing records in .pymobiledevice3 here.</summary>
    public static string IphoneHome => Folder(Data, "iphone");

    public static string DataFile(string name) => Path.Combine(Data, name);

    /// <summary>Points a Python child process at the app's data folder instead of the Windows user profile.</summary>
    public static void ApplyIphoneEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["USERPROFILE"] = IphoneHome;
        psi.Environment["WAILINK_DATA"] = Data;
        psi.Environment["WAILINK_LOGS"] = Logs;
    }

    static string ChooseDataFolder()
    {
        string local = Path.Combine(Root, "data");
        try
        {
            Directory.CreateDirectory(local);
            string probe = Path.Combine(local, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return local;
        }
        catch
        {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAI Link", "data");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    static string Folder(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
