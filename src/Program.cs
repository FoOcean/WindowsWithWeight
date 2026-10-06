using WindowsWithWeight.Testing;

namespace WindowsWithWeight;

public enum RunMode { Tray, SelfTest, Demo, Info, Exit, Help }

public sealed class Options
{
    public RunMode Mode = RunMode.Tray;
    public bool StartDisabled;
    public string Preset = "normal";
    public bool Stage;
    public string? RecordPath;
    public bool Verbose;
    public double StartDelay = 0.8;
    public string Grabber = "gdigrab";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            switch (a)
            {
                case "--selftest": case "--self-test": case "--test": o.Mode = RunMode.SelfTest; break;
                case "--demo": o.Mode = RunMode.Demo; break;
                case "--info": o.Mode = RunMode.Info; break;
                case "--exit": o.Mode = RunMode.Exit; break;
                case "--help": case "-h": case "/?": o.Mode = RunMode.Help; break;
                case "--disabled": o.StartDisabled = true; break;
                case "--stage": o.Stage = true; break;
                case "--verbose": case "-v": o.Verbose = true; break;
                case "--preset" when i + 1 < args.Length: o.Preset = args[++i]; break;
                case "--record" when i + 1 < args.Length: o.RecordPath = args[++i]; o.Stage = true; break;
                case "--grabber" when i + 1 < args.Length: o.Grabber = args[++i].ToLowerInvariant(); break;
                case "--start-delay" when i + 1 < args.Length:
                    o.StartDelay = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
        return o;
    }
}

internal static class Program
{
    public const string InstanceMutexName = @"Local\WindowsWithWeight.Instance";
    public const string ExitEventName = @"Local\WindowsWithWeight.Exit";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        ApplicationConfiguration.Initialize();

        switch (options.Mode)
        {
            case RunMode.Help:
                ConsoleHost.Attach();
                Console.WriteLine(HelpText);
                return 0;

            case RunMode.Exit:
                ConsoleHost.Attach();
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ExitEventName);
                    ev.Set();
                    Console.WriteLine("Asked the running Windows with Weight instance to exit.");
                    return 0;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Console.WriteLine("Windows with Weight is not running.");
                    return 1;
                }

            case RunMode.Info:
                ConsoleHost.Attach();
                return InfoCommand.Run();

            case RunMode.SelfTest:
                ConsoleHost.Attach();
                return TestHost.Run(ui => new SelfTest(options).Run(ui));

            case RunMode.Demo:
                ConsoleHost.Attach();
                return TestHost.Run(ui => new Demo(options).Run(ui));

            default:
                using (var mutex = new Mutex(true, InstanceMutexName, out bool created))
                {
                    if (!created)
                    {
                        MessageBox.Show("Windows with Weight is already running (see the tray).", "Windows with Weight",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 1;
                    }
                    Application.Run(new TrayApp(options));
                    return 0;
                }
        }
    }

    private const string HelpText = """
        Windows with Weight - give real windows momentum, collisions and gravity.

          WindowsWithWeight.exe              tray app (drag a window and let go while moving to throw it)
          WindowsWithWeight.exe --disabled   tray app, physics paused until you tick "Enabled"
          WindowsWithWeight.exe --preset light|normal|heavy
          WindowsWithWeight.exe --exit       close a running instance
          WindowsWithWeight.exe --selftest   spawn test windows, run checks, print PASS/FAIL, exit code 0 = all passed
          WindowsWithWeight.exe --demo [--stage] [--record out.mp4]
                                             scripted show with the app's own windows (for videos)
          WindowsWithWeight.exe --info       print monitors, DPI and which windows are eligible (read-only)

        Hotkeys (tray mode): Ctrl+Alt+G gravity on/off, Ctrl+Alt+R restore positions.
        """;
}

/// <summary>Gives a WinExe a console when started from one (or keeps redirected stdout).</summary>
internal static class ConsoleHost
{
    public static void Attach()
    {
        IntPtr h = Native.GetStdHandle(-11);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) Native.AttachConsole(-1);
    }
}

/// <summary>Runs a test/demo body on a worker thread while the UI thread pumps messages (needed for hooks and windows).</summary>
internal static class TestHost
{
    public static int Run(Func<UiThread, int> body)
    {
        var ctx = new ApplicationContext();
        var ui = new UiThread();
        int code = 3;
        var worker = new Thread(() =>
        {
            try { code = body(ui); }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL  unhandled exception: " + ex);
                code = 2;
            }
            finally
            {
                Console.Out.Flush();
                try { ui.Post(ctx.ExitThread); } catch { }
            }
        }) { IsBackground = true, Name = "WindowsWithWeight.TestBody" };
        ui.Post(worker.Start);
        Application.Run(ctx);
        worker.Join(3000);
        Console.Out.Flush();
        return code;
    }
}

/// <summary>Invokes onto the UI thread that created it.</summary>
public sealed class UiThread
{
    private readonly Control control;

    public UiThread()
    {
        control = new Control();
        control.CreateControl();
        _ = control.Handle;
    }

    public void Invoke(Action a) => control.Invoke(a);
    public T Invoke<T>(Func<T> f) => (T)control.Invoke(f)!;
    public void Post(Action a) => control.BeginInvoke(a);
}

internal static class InfoCommand
{
    public static int Run()
    {
        Console.WriteLine($"Windows with Weight {typeof(Program).Assembly.GetName().Version}");
        Console.WriteLine($"DPI awareness: {(Native.IsPerMonitorV2() ? "Per-Monitor V2" : "NOT per-monitor v2")}");
        Console.WriteLine($"Integrity level: 0x{WindowFilter.OwnIntegrityLevel:X}");
        foreach (var m in Monitors.All(withRefreshRate: true))
            Console.WriteLine($"Monitor {m.Device}{(m.Primary ? " (primary)" : "")}: bounds {m.Bounds} work {m.Work} scale {m.Scale:0.##} refresh {m.RefreshHz} Hz");
        var reasons = new Dictionary<string, int>();
        var eligible = new List<string>();
        foreach (var h in WindowFilter.TopLevelWindows())
        {
            if (!Native.IsWindowVisible(h)) { Count(reasons, "hidden"); continue; }
            string? why = WindowFilter.Reject(h, testMode: false);
            if (why == null)
            {
                WindowGeometry.TryGet(h, out var g);
                eligible.Add($"  {ProcessName(h),-28} {Native.ClassName(h),-40} visible {g.Visible} (insets L{g.InsetL} T{g.InsetT})");
            }
            else Count(reasons, why);
        }
        Console.WriteLine($"Eligible windows: {eligible.Count}");
        foreach (var e in eligible) Console.WriteLine(e);
        Console.WriteLine("Skipped: " + string.Join(", ", reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        return 0;
    }

    private static void Count(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out int v) ? v + 1 : 1;

    private static string ProcessName(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return pid.ToString(); }
    }
}
