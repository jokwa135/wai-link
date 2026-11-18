using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBridge;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(nint hWnd);

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--shell-preview")
        {
            ApplicationConfiguration.Initialize();
            try { using var form = new MainForm(); form.RenderPreview(args[1]); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "error.txt"), ex.ToString()); }
            Environment.Exit(0);
        }

        try
        {
            using var cur = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(cur.ProcessName))
            {
                using (other)
                {
                    if (other.Id == cur.Id) continue;
                    if (other.MainWindowHandle != 0)
                    {
                        ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(other.MainWindowHandle);
                        return;
                    }
                    try { other.Kill(true); } catch { }
                }
            }
        }
        catch { }

        var logFile = Path.Combine(AppPaths.Logs, "crash.log");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            try { File.AppendAllText(logFile, $"{DateTime.Now:o} UI: {e.Exception}\n"); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.AppendAllText(logFile, $"{DateTime.Now:o} Domain: {e.ExceptionObject}\n"); } catch { }
        };

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(logFile, $"{DateTime.Now:o} Main: {ex}\n"); } catch { }
        }
        finally
        {
            Environment.Exit(0);
        }
    }
}
