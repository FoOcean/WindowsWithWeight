using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using WindowsWithWeight.Physics;

namespace WindowsWithWeight.Testing;

/// <summary>
/// `--demo`: a ~14 s choreographed show for videos using only this app's own windows.
/// Every throw goes through the real drag path (MOVESIZESTART/END handlers, LOCATIONCHANGE samples,
/// velocity estimate, Engine.Release). A drawn hand shows where the "user" grabs; the real mouse is never moved.
/// `--stage` adds a 16:9 backdrop that becomes the arena (clean framing); `--record file.mp4` captures it with ffmpeg.
/// </summary>
public sealed class Demo
{
    private readonly Options options;
    private readonly PreciseTimer timer = new();
    private UiThread ui = null!;
    private Engine engine = null!;
    private DragWatcher watcher = null!;
    private StageWindow? stage;
    private HandOverlay? hand;
    private RectD area;
    private double kx, ky, k;
    private Stopwatch clock = new();
    private readonly Dictionary<string, TestWindow> win = new();

    public Demo(Options options) { this.options = options; }

    private RectD Design(double x, double y, double w, double h) =>
        RectD.FromXYWH(Math.Round(area.L + x * kx), Math.Round(area.T + y * ky), Math.Round(w * k), Math.Round(h * k));

    private void Caption(string text, string hint = "")
    {
        Console.WriteLine($"{clock.Elapsed.TotalSeconds,5:0.0}s  {text}{(hint.Length > 0 ? " - " + hint : "")}");
        if (stage != null) ui.Invoke(() => stage.SetCaption(text, hint));
    }

    private void At(double seconds)
    {
        double wait = seconds - clock.Elapsed.TotalSeconds;
        if (wait > 0) timer.Sleep(wait);
    }

    private IntPtr H(string name) => ui.Invoke(() => win[name].Handle);

    public int Run(UiThread ui)
    {
        this.ui = ui;
        var pm = Monitors.Primary(withRefreshRate: true);
        area = pm.Work;
        if (options.Stage)
        {
            double s = pm.Scale;
            double w = Math.Min(1280 * s, area.W - 40), h = Math.Min(720 * s, area.H - 40);
            if (w / h > 16.0 / 9) w = h * 16 / 9; else h = w * 9 / 16;
            w = Math.Floor(w / 2) * 2; h = Math.Floor(h / 2) * 2; // even sizes for H.264
            area = RectD.FromXYWH(Math.Round(area.CenterX - w / 2), Math.Round(area.CenterY - h / 2), w, h);
            var a = area;
            stage = ui.Invoke(() =>
            {
                var st = new StageWindow();
                st.Bounds = new Rectangle((int)a.L, (int)a.T, (int)a.W, (int)a.H);
                st.Show();
                st.Bounds = new Rectangle((int)a.L, (int)a.T, (int)a.W, (int)a.H);
                return st;
            });
        }
        kx = area.W / 1280.0;
        ky = area.H / 720.0;
        k = Math.Min(kx, ky);
        Console.WriteLine($"Demo arena {area} ({(options.Stage ? "stage" : "primary work area")}), display {pm.RefreshHz} Hz");

        var source = new TestWindowSource();
        engine = new Engine(source, new FixedArena(area), PhysicsSettings.Normal) { IgnoreReleasesInSnapZones = false };
        engine.Start();
        watcher = new DragWatcher(engine, (uint)Environment.ProcessId);
        ui.Invoke(() => watcher.Install());
        hand = ui.Invoke(() => new HandOverlay((int)Math.Round(70 * k)));
        Recorder? rec = null;
        try
        {
            var layout = new (string Name, string Glyph, Color Color, RectD Rect)[]
            {
                ("Paper plane", "plane", Color.FromArgb(59, 167, 255), Design(70, 110, 220, 150)),
                ("Gift", "gift", Color.FromArgb(139, 92, 246), Design(600, 120, 200, 150)),
                ("Book", "book", Color.FromArgb(240, 160, 30), Design(1030, 290, 230, 160)),
                ("Fridge", "fridge", Color.FromArgb(20, 160, 150), Design(40, 390, 190, 300)),
                ("Balloon", "balloon", Color.FromArgb(255, 92, 138), Design(300, 520, 170, 120)),
                ("Sofa", "sofa", Color.FromArgb(79, 107, 237), Design(650, 460, 390, 225)),
            };

            if (options.RecordPath != null)
            {
                // windows are already laid out before recording starts, but hidden: show stage first
                timer.Sleep(0.4);
                rec = Recorder.Start(options.RecordPath, area, options.Grabber, out string? err);
                if (rec == null) Console.WriteLine("Recording disabled: " + err);
                else timer.Sleep(1.2); // ffmpeg warm-up
            }
            else timer.Sleep(Math.Max(0, options.StartDelay));

            clock = Stopwatch.StartNew();
            foreach (var (name, glyph, color, rect) in layout)
            {
                var w = ui.Invoke(() =>
                {
                    var f = new TestWindow(name, glyph, color, topMost: true);
                    f.ShowAtVisible(rect);
                    return f;
                });
                win[name] = w;
                source.Register(H(name));
                timer.Sleep(0.06);
            }
            ui.Invoke(() =>
            {
                hand.Show();
                Native.SetWindowPos(hand.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                hand.Present(-32000, -32000, 0);
            });

            // 1. throw: equal masses trade momentum, the hit one bounces off the wall
            At(0.5);
            Caption("Throw a window", "it keeps its momentum");
            At(0.9);
            Throw("Paper plane", 150, 0, windup: 0.35, seconds: 0.11);

            // 2. light into heavy
            At(3.0);
            Caption("Light vs. heavy", "mass = window area");
            At(3.3);
            Throw("Balloon", 110, 0, windup: 0.4, seconds: 0.10);

            // 3. heavy shoves light
            At(5.3);
            Caption("Heavy shoves light");
            At(5.6);
            Throw("Sofa", -150, 0, windup: 0.35, seconds: 0.12);

            // 4. gravity
            At(7.6);
            Caption("Gravity", "Ctrl+Alt+G");
            engine.SetGravity(true, (int)area.CenterX, (int)area.CenterY);

            // 5. toss one onto the pile
            At(10.0);
            Caption("Toss one on the pile");
            At(10.2);
            var plane = Native.GetWindowRect(H("Paper plane"), out var pr) ? pr.ToRectD() : area;
            double dir = plane.CenterX < area.CenterX ? 1 : -1;
            Throw("Paper plane", dir * 100, -115, windup: 0.3, seconds: 0.12);

            // 6. restore
            At(12.6);
            Caption("Restore positions", "Ctrl+Alt+R");
            engine.RestoreAll();
            At(14.4);
            Caption("");
            At(14.8);

            var t = engine.Inspect((tl, _) => (tl.Collisions.Count, tl.Bounces.Count, tl.Fps, tl.Releases.Count(r => r.Flung)));
            Console.WriteLine($"done: {t.Item4} throws, {t.Item1} collisions, {t.Item2} wall bounces, {t.Item3:0} fps");
        }
        finally
        {
            rec?.Stop();
            ui.Invoke(() => watcher.Dispose());
            engine.Dispose();
            foreach (var w in win.Values) ui.Invoke(w.Close);
            if (hand != null) ui.Invoke(hand.Close);
            if (stage != null) ui.Invoke(stage.Close);
            timer.Dispose();
        }
        if (rec != null && options.RecordPath != null)
        {
            var fi = new FileInfo(options.RecordPath);
            Console.WriteLine(fi.Exists ? $"recorded {fi.FullName} ({fi.Length / 1024} KB)" : "recording failed: " + rec.Error);
            return fi.Exists ? 0 : 1;
        }
        return 0;
    }

    private void Throw(string name, double dx, double dy, double windup, double seconds)
    {
        var r = ScriptedDrag.Throw(ui, watcher, H(name), dx * k, dy * k, seconds, timer, windup, hand, holdBefore: 0.18);
        Console.WriteLine($"{clock.Elapsed.TotalSeconds,5:0.0}s    {name}: release {r?.Vx.ToString("0", CultureInfo.InvariantCulture)},{r?.Vy.ToString("0", CultureInfo.InvariantCulture)} px/s -> {r?.Note}");
    }
}

/// <summary>Captures a screen rectangle with ffmpeg's gdigrab (mouse cursor not drawn).</summary>
internal sealed class Recorder
{
    private readonly Process process;
    private readonly System.Text.StringBuilder log = new();
    public string Error => log.ToString();

    private Recorder(Process p)
    {
        process = p;
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        p.BeginErrorReadLine();
    }

    /// <param name="grabber">"gdigrab" (default, virtual-screen coordinates) or "ddagrab" (Desktop Duplication,
    /// smoother 60 fps; assumes the primary monitor is DXGI output 0).</param>
    public static Recorder? Start(string path, RectD rect, string grabber, out string? error)
    {
        error = null;
        var r = rect.ToRECT();
        string input;
        if (grabber == "ddagrab")
        {
            var mon = Monitors.Primary().Bounds.ToRECT();
            input = $"-f lavfi -i ddagrab=output_idx=0:framerate=60:draw_mouse=0:offset_x={r.Left - mon.Left}:offset_y={r.Top - mon.Top}:video_size={r.Width}x{r.Height} -vf hwdownload,format=bgra";
        }
        else
        {
            input = $"-f gdigrab -framerate 60 -draw_mouse 0 -offset_x {r.Left} -offset_y {r.Top} -video_size {r.Width}x{r.Height} -i desktop";
        }
        string args = string.Join(' ',
            "-y -hide_banner -loglevel error",
            input,
            "-c:v libx264 -preset veryfast -crf 17 -pix_fmt yuv420p -movflags +faststart",
            $"\"{Path.GetFullPath(path)}\"");
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi);
            if (p == null) { error = "ffmpeg did not start"; return null; }
            return new Recorder(p);
        }
        catch (Exception ex)
        {
            error = "ffmpeg not available (" + ex.Message + ")";
            return null;
        }
    }

    public void Stop()
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Write("q");
                process.StandardInput.Flush();
            }
            if (!process.WaitForExit(15000)) process.Kill();
        }
        catch { }
    }
}
