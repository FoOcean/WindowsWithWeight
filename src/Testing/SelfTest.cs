using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using WindowsWithWeight.Physics;

namespace WindowsWithWeight.Testing;

/// <summary>
/// `--selftest`: pure-math checks of the physics, then end-to-end checks that spawn this app's own
/// windows and drive them through the same code paths as real use (drag handlers + LOCATIONCHANGE
/// hook + simulation thread + async SetWindowPos). The engine is built with a
/// <see cref="TestWindowSource"/>, so it is structurally unable to move any other window.
/// The mouse is never moved and no input is synthesised.
/// </summary>
public sealed class SelfTest
{
    private readonly Options options;
    private readonly List<string> failures = new();
    private readonly PreciseTimer timer = new();
    private readonly TestWindowSource source = new();
    private readonly List<TestWindow> windows = new();
    private readonly HashSet<IntPtr> everCreated = new();
    private int pass, fail;
    private UiThread ui = null!;
    private Engine engine = null!;
    private DragWatcher watcher = null!;
    private RectD arena;
    private double u, scale;
    private TestWindow t1 = null!, t2 = null!, t3 = null!, t4 = null!;
    private readonly Dictionary<IntPtr, SavedPos> s0 = new();

    public SelfTest(Options options) { this.options = options; }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
        if (ok) pass++;
        else { fail++; failures.Add(name); }
    }

    private static void Info(string text) => Console.WriteLine("INFO  " + text);
    private static void Section(string title) { Console.WriteLine(); Console.WriteLine("== " + title); }

    public int Run(UiThread ui)
    {
        this.ui = ui;
        var total = Stopwatch.StartNew();
        Console.WriteLine("Windows with Weight self-test");
        Console.WriteLine("Only windows created by this test are moved; the mouse is never touched.");
        Dictionary<IntPtr, RECT> userBefore = SnapshotUserWindows();
        try
        {
            Section("Setup");
            Check("process is Per-Monitor V2 DPI aware", Native.IsPerMonitorV2() && ui.Invoke(Native.IsPerMonitorV2));

            Section("Physics unit checks (no windows)");
            VelocityChecks();
            WallChecks();
            MultiMonitorChecks();
            CollisionChecks();
            GravityUnitChecks();
            GrabbableChecks();

            Section("Eligibility of real system windows (read-only)");
            SystemEligibilityChecks();

            Section("Live checks with the app's own test windows");
            LiveChecks();
        }
        catch (Exception ex)
        {
            Check("no unexpected exception", false, ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex);
        }
        finally
        {
            Cleanup();
        }

        Section("User windows");
        var userAfter = SnapshotUserWindows();
        int changed = userBefore.Count(kv => userAfter.TryGetValue(kv.Key, out var r) && !Same(r, kv.Value));
        Info($"{userBefore.Count} visible windows of other apps before the test; {changed} changed position or size during it" +
             (changed > 0 ? " (by you or their apps; the engine never targeted them, see the check above)" : ""));

        Console.WriteLine();
        Console.WriteLine($"{pass} passed, {fail} failed in {total.Elapsed.TotalSeconds:0.0}s");
        foreach (var f in failures) Console.WriteLine("  failed: " + f);
        Console.WriteLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
        return fail == 0 ? 0 : 1;
    }

    // ================================================================== unit checks

    private static PhysicsWorld World(Bounds b, PhysicsSettings? s = null) => new(b) { Settings = s ?? PhysicsSettings.Normal };

    private static Body Free(int id, RectD r, Bounds b, double vx = 0, double vy = 0) =>
        new(new IntPtr(id), r, 0, 0, 1, b, BodyKind.Free) { VX = vx, VY = vy };

    private static void Simulate(PhysicsWorld w, double seconds, Action? afterStep = null)
    {
        int steps = (int)(seconds / PhysicsWorld.Dt);
        for (int i = 0; i < steps; i++)
        {
            w.Step(PhysicsWorld.Dt);
            w.Finished.Clear();
            afterStep?.Invoke();
            if (w.Bodies.Count == 0) break;
        }
    }

    private void VelocityChecks()
    {
        var steady = new List<(double, double, double)>();
        for (int i = 0; i <= 40; i++) steady.Add((i * 0.008, 100 + 1500 * i * 0.008, 50));
        var (vx, vy, _) = VelocityEstimator.Estimate(steady, Engine.VelocityWindow);
        Check("release velocity: steady 1500 px/s drag", Math.Abs(vx - 1500) < 15 && Math.Abs(vy) < 1, $"estimated {F(vx)},{F(vy)} px/s");

        var pause = new List<(double, double, double)>();
        for (int i = 0; i <= 25; i++) pause.Add((i * 0.008, 2000 * i * 0.008, 0));
        for (double t = 0.216; t <= 0.33; t += 0.016) pause.Add((t, 400, 0)); // sim-thread samples while held still
        var (px, _, _) = VelocityEstimator.Estimate(pause, Engine.VelocityWindow);
        Check("release velocity: holding still before letting go gives ~0", Math.Abs(px) < 1, $"estimated {F(px)} px/s");

        var flick = new List<(double, double, double)>();
        for (double t = 0; t <= 0.3001; t += 0.016) flick.Add((t, 0, 0));
        for (int i = 1; i <= 15; i++) flick.Add((0.3 + i * 0.004, 3000 * i * 0.004, 0));
        double expect = 3000 * 0.06 / Engine.VelocityWindow;
        var (fx, _, _) = VelocityEstimator.Estimate(flick, Engine.VelocityWindow);
        Check("release velocity: short flick after a pause", Math.Abs(fx - expect) / expect < 0.05, $"estimated {F(fx)} expected {F(expect)} px/s");
    }

    private void WallChecks()
    {
        var bounds = new Bounds(new RectD(0, 0, 1000, 800));
        var w = World(bounds);
        var bounces = new List<BounceEvent>();
        w.Bounced = bounces.Add;
        var b = Free(1, RectD.FromXYWH(700, 300, 200, 100), bounds, 3000);
        w.Add(b, true);
        Simulate(w, 6);
        var e = bounces.FirstOrDefault();
        Check("walls: window stops exactly at the work-area edge", bounces.Count > 0 && e.AxisX && Math.Abs(e.Edge - 1000) < 0.01,
            $"contact edge {e.Edge:0.000} vs wall 1000");
        Check("walls: restitution ~0.5 (Normal)", bounces.Count > 0 && Math.Abs(-e.SpeedOut / e.SpeedIn - 0.5) < 0.01,
            $"{F(e.SpeedIn)} -> {F(e.SpeedOut)} px/s");
        Check("walls: slides to rest inside the work area", w.Bodies.Count == 0 && bounds.IsInside(b.Rect, 0.01), $"rest {b.Rect}");
    }

    private void MultiMonitorChecks()
    {
        // Side-by-side monitors whose tops differ: the strip above the right monitor is a gap.
        var side = new Bounds(new[] { new RectD(0, 0, 1920, 1040), new RectD(1920, 300, 3840, 1340) });
        var w = World(side);
        var a = Free(1, RectD.FromXYWH(1500, 100, 300, 200), side, 3000);
        w.Add(a, true);
        double maxR = 0;
        Simulate(w, 6, () => maxR = Math.Max(maxR, a.X + a.W));
        Check("multi-monitor: the gap beside an offset monitor is a wall", maxR <= 1920.01 && a.WallBounces > 0,
            $"max right edge {maxR:0.00}, bounces {a.WallBounces}");

        var w2 = World(side);
        var c = Free(2, RectD.FromXYWH(1500, 500, 300, 200), side, 3000);
        w2.Add(c, true);
        double maxR2 = 0;
        Simulate(w2, 6, () => maxR2 = Math.Max(maxR2, c.X + c.W));
        Check("multi-monitor: windows cross where monitors touch", maxR2 > 2000 && side.IsInside(c.Rect, 0.01),
            $"max right edge {maxR2:0}, rest {c.Rect}");

        // Vertically stacked monitors with the taskbar between them (this machine's layout).
        var stack = new Bounds(new[] { new RectD(0, 0, 2560, 1392), new RectD(0, 1440, 2560, 3040) });
        var w3 = World(stack);
        var d = Free(3, RectD.FromXYWH(400, 900, 500, 300), stack, 0, 3000);
        w3.Add(d, true);
        double maxB = 0;
        Simulate(w3, 6, () => maxB = Math.Max(maxB, d.Y + d.H));
        Check("multi-monitor: a taskbar strip between monitors is a wall", Math.Abs(maxB - 1392) < 0.01 && d.WallBounces > 0,
            $"max bottom {maxB:0.000}");

        var live = new LiveArena().FreeBounds();
        var mons = Monitors.All(withRefreshRate: true);
        foreach (var m in mons)
            Info($"monitor {m.Device}{(m.Primary ? " (primary)" : "")}: bounds {m.Bounds} work {m.Work} scale {F(m.Scale)} {m.RefreshHz} Hz");
        Check("multi-monitor: live walls are the union of every work area",
            mons.All(m => live.Uncovered(m.Work) < 0.5) && live.Areas.Count == mons.Count, $"{live.Areas.Count} areas");
    }

    private void CollisionChecks()
    {
        var big = new Bounds(new RectD(-10000, -10000, 20000, 20000));

        // light (200x100) thrown at a resting heavy window (600x400): the resting window is woken and pushed
        var w = World(big);
        var cols = new List<CollisionEvent>();
        w.Collided = cols.Add;
        var light = Free(10, RectD.FromXYWH(0, 0, 200, 100), big, 2000);
        w.Obstacles[new IntPtr(11)] = new Obstacle(new IntPtr(11), RectD.FromXYWH(400, -100, 600, 400), 0, 0, 1);
        w.Add(light, true);
        Simulate(w, 5);
        var c = cols.FirstOrDefault();
        bool hit = cols.Count > 0 && c.A == new IntPtr(10) && c.B == new IntPtr(11);
        double pBefore = c.MassA * c.VaBefore + c.MassB * c.VbBefore, pAfter = c.MassA * c.VaAfter + c.MassB * c.VbAfter;
        Check("collisions: a resting window that is hit wakes up and gets pushed", hit && c.VbAfter > 0, $"heavy after {F(c.VbAfter)} px/s");
        Check("collisions: momentum is conserved (mass = area)", hit && Math.Abs(pBefore - pAfter) <= 1e-9 * Math.Abs(pBefore),
            $"p before {pBefore:0} after {pAfter:0}");
        Check("collisions: a small window bounces off a big one", hit && c.VaAfter < 0 && c.VbAfter < 0.2 * c.VaBefore,
            $"light {F(c.VaBefore)} -> {F(c.VaAfter)}, heavy 0 -> {F(c.VbAfter)} px/s");
        double e = hit ? (c.VbAfter - c.VaAfter) / (c.VaBefore - c.VbBefore) : 0;
        Check("collisions: window-window restitution ~0.5", Math.Abs(e - 0.5) < 0.01, $"e = {F(e)}");

        // heavy thrown at a light one: the light one leaves faster than the heavy one arrived
        var w2 = World(big);
        var cols2 = new List<CollisionEvent>();
        w2.Collided = cols2.Add;
        var heavy = Free(20, RectD.FromXYWH(0, 0, 600, 400), big, 1500);
        w2.Obstacles[new IntPtr(21)] = new Obstacle(new IntPtr(21), RectD.FromXYWH(700, 100, 200, 100), 0, 0, 1);
        w2.Add(heavy, true);
        Simulate(w2, 5);
        var c2 = cols2.FirstOrDefault();
        Check("collisions: a big window shoves a small one", cols2.Count > 0 && c2.VbAfter > c2.VaBefore && c2.VaAfter > 0,
            $"heavy {F(c2.VaBefore)} -> {F(c2.VaAfter)}, light 0 -> {F(c2.VbAfter)} px/s");

        // windows overlapping at throw time pass through each other, later contacts still collide
        var w3 = World(big);
        var cols3 = new List<CollisionEvent>();
        w3.Collided = cols3.Add;
        w3.Obstacles[new IntPtr(31)] = new Obstacle(new IntPtr(31), RectD.FromXYWH(100, 100, 400, 300), 0, 0, 1);
        w3.Obstacles[new IntPtr(32)] = new Obstacle(new IntPtr(32), RectD.FromXYWH(1100, 0, 300, 300), 0, 0, 1);
        var mover = Free(30, RectD.FromXYWH(0, 0, 400, 300), big, 2600);
        w3.Add(mover, true);
        Simulate(w3, 5);
        Check("collisions: windows overlapping at release are ghosted, later hits collide",
            !cols3.Any(x => x.B == new IntPtr(31) || x.A == new IntPtr(31)) && cols3.Any(x => x.B == new IntPtr(32)),
            $"{cols3.Count} collision(s)");
    }

    private void GravityUnitChecks()
    {
        var bounds = new Bounds(new RectD(0, 0, 1000, 800));
        var w = World(bounds);
        var top = new Body(new IntPtr(40), RectD.FromXYWH(120, 50, 300, 200), 0, 0, 1, bounds, BodyKind.Gravity);
        var bottom = new Body(new IntPtr(41), RectD.FromXYWH(50, 380, 400, 150), 0, 0, 1, bounds, BodyKind.Gravity);
        var aside = new Body(new IntPtr(42), RectD.FromXYWH(600, 100, 250, 250), 0, 0, 1, bounds, BodyKind.Gravity);
        w.Add(top, false); w.Add(bottom, false); w.Add(aside, false);
        w.GhostDeepOverlaps(new[] { top, bottom, aside }, 0.35);
        Simulate(w, 4);
        Check("gravity: windows land exactly on the floor", Math.Abs(bottom.Rect.B - 800) < 0.01 && Math.Abs(aside.Rect.B - 800) < 0.01,
            $"bottoms {bottom.Rect.B:0.000}, {aside.Rect.B:0.000}");
        Check("gravity: a window above another stacks on it", Math.Abs(top.Rect.B - bottom.Rect.T) < 0.5,
            $"gap {top.Rect.B - bottom.Rect.T:0.000}");
        Check("gravity: the pile goes to sleep", w.AllAsleep);
    }

    private void GrabbableChecks()
    {
        var gb = new Bounds(new RectD(0, 0, 1920, 1040));
        var off = RectD.FromXYWH(-700, -500, 800, 600);
        var fixedR = gb.MakeGrabbable(off, 32, 120);
        Check("safety: a window whose title bar is off-screen is pulled back to a grabbable strip",
            !gb.IsGrabbable(off, 32, 120) && gb.IsGrabbable(fixedR, 32, 120), $"{off} -> {fixedR}");
        var inside = RectD.FromXYWH(100, 100, 400, 300);
        Check("safety: grabbable windows are left alone", gb.MakeGrabbable(inside, 32, 120) == inside);
    }

    private void SystemEligibilityChecks()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        Check("eligibility: taskbar is skipped", taskbar != IntPtr.Zero && WindowFilter.Reject(taskbar, false) == "shell",
            WindowFilter.Reject(taskbar, false) ?? "eligible?!");
        IntPtr desktop = Native.GetShellWindow();
        Check("eligibility: desktop is skipped", desktop != IntPtr.Zero && WindowFilter.Reject(desktop, false) != null,
            WindowFilter.Reject(desktop, false) ?? "eligible?!");
        var sys = Process.GetProcessesByName("winlogon").FirstOrDefault() ?? Process.GetProcessesByName("csrss").FirstOrDefault();
        Check("eligibility: windows of higher-integrity (elevated/system) processes are skipped",
            sys != null && !WindowFilter.CanMoveProcess((uint)sys.Id) && WindowFilter.CanMoveProcess((uint)Environment.ProcessId),
            sys != null ? $"{sys.ProcessName} pid {sys.Id}; our integrity 0x{WindowFilter.OwnIntegrityLevel:X}" : "no system process found");
        int eligible = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var h in WindowFilter.TopLevelWindows())
        {
            if (!Native.IsWindowVisible(h)) continue;
            var why = WindowFilter.Reject(h, false);
            if (why == null) eligible++;
            else reasons[why] = reasons.TryGetValue(why, out int n) ? n + 1 : 1;
        }
        Info($"live mode would see {eligible} eligible window(s); skipped: " +
             string.Join(", ", reasons.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string cls, string? name);

    // ================================================================== live checks

    private TestWindow Spawn(string label, string glyph, Color color, RectD visible, bool tool = false)
    {
        var w = ui.Invoke(() =>
        {
            var f = new TestWindow(label, glyph, color, topMost: false, toolWindow: tool);
            f.ShowAtVisible(visible);
            return f;
        });
        IntPtr h = ui.Invoke(() => w.Handle);
        source.Register(h);
        everCreated.Add(h);
        windows.Add(w);
        return w;
    }

    private static IntPtr H(TestWindow w) => (IntPtr)w.Invoke(() => w.Handle);

    private WinGeom Geo(TestWindow w)
    {
        WindowGeometry.TryGet(H(w), out var g);
        return g;
    }

    private bool WaitIdle(double timeout = 8)
    {
        var sw = Stopwatch.StartNew();
        timer.Sleep(0.05);
        while (sw.Elapsed.TotalSeconds < timeout)
        {
            if (!engine.IsBusy)
            {
                timer.Sleep(0.15); // let queued async SetWindowPos calls land
                if (!engine.IsBusy) return true;
            }
            timer.Sleep(0.02);
        }
        return false;
    }

    private bool AtS0(TestWindow w) => Native.GetWindowRect(H(w), out var r) && s0.TryGetValue(H(w), out var p) && r.Left == p.X && r.Top == p.Y;

    private void RestoreAndCheck(string label)
    {
        engine.RestoreAll();
        bool idle = WaitIdle(6);
        var off = windows.Where(w => s0.ContainsKey(H(w)) && !AtS0(w)).Select(w => w.Label).ToList();
        Check($"restore positions animates every window back exactly ({label})", idle && off.Count == 0 && engine.SavedPositionCount == 0,
            off.Count > 0 ? "not restored: " + string.Join(", ", off) : $"{s0.Count} windows at their original pixels");
    }

    private List<CollisionEvent> CollisionsSince(int index) => engine.Inspect((t, _) => t.Collisions.Skip(index).ToList());
    private int CollisionCount => engine.Inspect((t, _) => t.Collisions.Count);
    private List<BounceEvent> BouncesSince(int index) => engine.Inspect((t, _) => t.Bounces.Skip(index).ToList());
    private int BounceCount => engine.Inspect((t, _) => t.Bounces.Count);

    private bool InsideArena(TestWindow w, out RectD vis)
    {
        vis = Geo(w).VisibleD;
        return new Bounds(arena).IsInside(vis, 0.75);
    }

    private void LiveChecks()
    {
        var pm = Monitors.Primary(withRefreshRate: true);
        arena = pm.Work;
        scale = pm.Scale;
        double fit = Math.Min(1.0, Math.Min(arena.W / (1400 * scale), arena.H / (760 * scale)));
        u = scale * fit;
        Info($"arena = primary monitor work area {arena}, scale {F(scale)}, refresh {pm.RefreshHz} Hz");

        engine = new Engine(source, new FixedArena(arena), PhysicsSettings.Normal) { IgnoreReleasesInSnapZones = false };
        engine.Start();
        watcher = new DragWatcher(engine, (uint)Environment.ProcessId);
        Check("WinEvent hook installs (scoped to this process for the test)", ui.Invoke(watcher.Install));

        double L = arena.L, T = arena.T, B = arena.B;
        var r1 = RectD.FromXYWH(L + 100 * u, T + 140 * u, 240 * u, 160 * u);
        var r3 = RectD.FromXYWH(L + Math.Max(560 * u, arena.W * 0.5), T + 100 * u, 520 * u, 330 * u);
        var r4 = RectD.FromXYWH(r3.L + 90 * u, B - 230 * u, 300 * u, 200 * u);
        var r2 = RectD.FromXYWH(L + 100 * u, B - 270 * u, 360 * u, 240 * u);
        t1 = Spawn("Balloon", "balloon", Color.FromArgb(255, 92, 138), r1);
        t2 = Spawn("Book", "book", Color.FromArgb(240, 160, 30), r2);
        t3 = Spawn("Sofa", "sofa", Color.FromArgb(79, 107, 237), r3);
        t4 = Spawn("Gift", "gift", Color.FromArgb(139, 92, 246), r4);
        timer.Sleep(0.3);
        foreach (var w in windows)
        {
            Native.GetWindowRect(H(w), out var r);
            s0[H(w)] = new SavedPos(r.Left, r.Top);
        }
        var g1 = Geo(t1);
        Check("test windows are eligible for the test engine", windows.All(w => engine.Reject(H(w)) == null),
            string.Join(", ", windows.Select(w => engine.Reject(H(w)) ?? "ok")));
        Check("live mode would never touch them", windows.All(w => WindowFilter.Reject(H(w), testMode: false) == "own window"));
        Check("test windows have invisible borders (so visible bounds matter)", g1.InsetL > 0,
            $"window {g1.Window} visible {g1.Visible}");

        ScenarioThrowLightIntoHeavy();
        RestoreAndCheck("after throw");
        ScenarioHeavyIntoLight();
        RestoreAndCheck("after shove");
        ScenarioWalls();
        ScenarioCatch();
        RestoreAndCheck("after catch");
        ScenarioDragEdgeCases();
        ScenarioHungApp();
        ScenarioGravity();
        ScenarioEligibility();

        var outside = engine.Inspect((t, _) => t.MaxOutsidePx);
        Check("windows never left the work area while simulated", outside < 0.5, $"max overshoot {F(outside)} px");
        ScenarioGrabbable();

        var moved = engine.Inspect((t, _) => t.MovedWindows.ToList());
        var stray = moved.Where(h => !everCreated.Contains(h)).ToList();
        Check("the engine only ever moved this test's own windows", stray.Count == 0 && moved.Count > 0,
            $"{moved.Count} distinct windows moved, {stray.Count} foreign");
        double fps = engine.Inspect((t, _) => t.Fps);
        long frames = engine.Inspect((t, _) => t.ActiveFrames);
        Check("simulation runs at display refresh", fps >= Math.Min(50, pm.RefreshHz * 0.8),
            $"{F(fps)} fps over {frames} active frames, display {pm.RefreshHz} Hz, physics step 1/{1 / PhysicsWorld.Dt:0} s");
    }

    private void ScenarioThrowLightIntoHeavy()
    {
        Console.WriteLine("-- throw: drag the light 'Balloon' to the right and let go; it hits the heavy 'Sofa'");
        int c0 = CollisionCount;
        var start = Geo(t1).VisibleD;
        var start3 = Geo(t3).VisibleD;
        double dist = 240 * u, secs = 0.14;
        int loc0 = watcher.LocationEvents;
        var rel = ScriptedDrag.Throw(ui, watcher, H(t1), dist, 0, secs, timer);
        double expect = ScriptedDrag.ExpectedSpeed(dist, secs, Engine.VelocityWindow);
        Check("drag path: LOCATIONCHANGE hook delivered position samples", watcher.LocationEvents - loc0 >= 5,
            $"{watcher.LocationEvents - loc0} events");
        Check("drag path: release velocity estimated from the last ~70 ms", rel != null && Math.Abs(rel.Vx - expect) / expect < 0.25 && Math.Abs(rel.Vy) < 0.1 * expect,
            rel == null ? "no release" : $"estimated {F(rel.Vx)},{F(rel.Vy)} px/s from {rel.Samples} samples, expected ~{F(expect)}");
        Check("drag path: a fast release turns into a throw", rel != null && rel.Flung && engine.KindOf(H(t1)) == BodyKind.Free,
            rel?.Note ?? "");
        timer.Sleep(0.1);
        var during = Geo(t1).VisibleD;
        Check("throw: the window keeps sliding after release", during.L > start.L + dist + 20 * u,
            $"released at x={F(start.L + dist)}, 100 ms later x={F(during.L)}");
        bool idle = WaitIdle();
        Check("throw: everything comes to rest", idle);
        var hits = CollisionsSince(c0).Where(c => (c.A == H(t1) && c.B == H(t3)) || (c.A == H(t3) && c.B == H(t1))).ToList();
        var hc = hits.FirstOrDefault();
        bool t1IsA = hits.Count > 0 && hc.A == H(t1);
        double lightBefore = t1IsA ? hc.VaBefore : hc.VbBefore, lightAfter = t1IsA ? hc.VaAfter : hc.VbAfter;
        double heavyAfter = t1IsA ? hc.VbAfter : hc.VaAfter;
        Check("collision: light window hit the heavy one and bounced back", hits.Count > 0 && lightAfter < 0 && heavyAfter > 0,
            hits.Count == 0 ? "no collision" : $"light {F(lightBefore)} -> {F(lightAfter)}, heavy 0 -> {F(heavyAfter)} px/s");
        var end3 = Geo(t3).VisibleD;
        Check("collision: the heavy window was nudged, much less than the light one travelled",
            end3.L - start3.L > 2 && end3.L - start3.L < (during.L - start.L),
            $"heavy moved {F(end3.L - start3.L)} px");
        Check("throw: windows rest inside the work area", windows.All(w => InsideArena(w, out _)));
    }

    private void ScenarioHeavyIntoLight()
    {
        Console.WriteLine("-- shove: throw the heavy 'Sofa' down onto the light 'Gift'");
        int c0 = CollisionCount, b0 = BounceCount;
        var startGift = Geo(t4).VisibleD;
        bool ok = engine.Fling(H(t3), 0, 2400 * u);
        Check("fling API accepts an eligible window", ok);
        bool idle = WaitIdle();
        var hits = CollisionsSince(c0).Where(c => (c.A == H(t3) && c.B == H(t4)) || (c.A == H(t4) && c.B == H(t3))).ToList();
        var hc = hits.FirstOrDefault();
        bool sofaIsA = hits.Count > 0 && hc.A == H(t3);
        double heavyBefore = sofaIsA ? hc.VaBefore : hc.VbBefore, heavyAfter = sofaIsA ? hc.VaAfter : hc.VbAfter;
        double lightAfter = sofaIsA ? hc.VbAfter : hc.VaAfter;
        Check("collision: the big window shoves the small one faster than itself", idle && hits.Count > 0 && lightAfter > heavyAfter && lightAfter > 0.9 * heavyBefore,
            hits.Count == 0 ? "no collision" : $"heavy {F(heavyBefore)} -> {F(heavyAfter)}, light 0 -> {F(lightAfter)} px/s");
        var floorBounce = BouncesSince(b0).Any(b => b.Hwnd == H(t4) && !b.AxisX);
        Check("bounce: the shoved window bounced off the bottom of the work area", floorBounce);
        Check("shove: windows rest inside the work area", windows.All(w => InsideArena(w, out _)));
    }

    private void ScenarioWalls()
    {
        Console.WriteLine("-- walls: throw 'Book' into the left edge");
        int b0 = BounceCount;
        engine.Fling(H(t2), -2600 * u, 0);
        bool idle = WaitIdle();
        var bounce = BouncesSince(b0).FirstOrDefault(b => b.Hwnd == H(t2) && b.AxisX);
        Check("bounce: visible frame touches the left edge exactly, restitution ~0.5",
            idle && bounce.Hwnd == H(t2) && Math.Abs(bounce.Edge - arena.L) < 0.01 && Math.Abs(-bounce.SpeedOut / bounce.SpeedIn - 0.5) < 0.02,
            $"contact at x={bounce.Edge:0.000} (edge {arena.L}), {F(bounce.SpeedIn)} -> {F(bounce.SpeedOut)} px/s");
        RestoreAndCheck("after bounce");

        Console.WriteLine("-- walls: same throw with restitution 0, so it rests against the edge");
        var normal = engine.Settings;
        engine.Settings = normal with { Name = "Stick", Restitution = 0 };
        engine.Fling(H(t2), -2600 * u, 0);
        WaitIdle();
        var g = Geo(t2);
        Check("no gap: the visible frame rests flush with the edge, the invisible border hangs outside",
            Math.Abs(g.Visible.Left - arena.L) <= 1 && g.Window.Left < arena.L,
            $"visible left {g.Visible.Left}, window-rect left {g.Window.Left}, edge {arena.L}");
        engine.Settings = normal;
        RestoreAndCheck("after resting on the edge");
    }

    private void ScenarioCatch()
    {
        Console.WriteLine("-- catch: grab 'Gift' mid-flight");
        engine.Fling(H(t4), -1800 * u, 0);
        timer.Sleep(0.12);
        bool flying = engine.IsSimulating(H(t4));
        ui.Invoke(() => watcher.HandleStart(H(t4))); // what EVENT_SYSTEM_MOVESIZESTART triggers
        bool stopped = !engine.IsSimulating(H(t4));
        timer.Sleep(0.06);
        Native.GetWindowRect(H(t4), out var a);
        timer.Sleep(0.3);
        Native.GetWindowRect(H(t4), out var b);
        Check("catch: grabbing a flying window stops its simulation immediately", flying && stopped);
        Check("catch: the caught window stays put", a.Left == b.Left && a.Top == b.Top, $"{a} then {b}");
        var rel = ui.Invoke(() => watcher.HandleEnd(H(t4)));
        Check("catch: letting go without moving does not throw it", rel != null && !rel.Flung,
            rel == null ? "no release" : $"{F(rel.Speed)} px/s, {rel.Note}");
    }

    private void ScenarioDragEdgeCases()
    {
        Console.WriteLine("-- drag edge cases: a resize is not a throw; a size change when the drag begins (un-maximize) is fine");
        IntPtr h = H(t2);
        WindowGeometry.TryGet(h, out var g0);
        var vis0 = g0.VisibleD;
        const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;

        // fast drag of the left border: position and size change together until release
        ui.Invoke(() => watcher.HandleStart(h));
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 0.15)
        {
            double f = sw.Elapsed.TotalSeconds / 0.15;
            int dx = (int)(140 * u * f * f);
            Native.SetWindowPos(h, IntPtr.Zero, g0.Window.Left - dx, g0.Window.Top, g0.Window.Width + dx, g0.Window.Height, flags);
            timer.Sleep(0.007);
        }
        var rel = ui.Invoke(() => watcher.HandleEnd(h));
        Check("drag path: a fast resize is not mistaken for a throw", rel != null && !rel.Flung && rel.Note == "resized or snapped",
            rel == null ? "no release" : $"{F(rel.Speed)} px/s, {rel.Note}");
        ui.Invoke(() => t2.PlaceVisible(vis0));
        timer.Sleep(0.1);

        // size jumps right after MOVESIZESTART (like a maximized window restoring under the cursor), then a throw
        ui.Invoke(() => watcher.HandleStart(h));
        Native.SetWindowPos(h, IntPtr.Zero, g0.Window.Left + (int)(20 * u), g0.Window.Top, g0.Window.Width - (int)(40 * u), g0.Window.Height - (int)(30 * u), flags);
        timer.Sleep(0.25);
        var rel2 = ScriptedDrag.Throw(ui, watcher, h, 200 * u, 0, 0.14, timer, beginDrag: false);
        Check("drag path: a window that changed size as the drag began can still be thrown", rel2 != null && rel2.Flung,
            rel2 == null ? "no release" : $"{F(rel2.Speed)} px/s, {rel2.Note}");
        WaitIdle();
        engine.RestoreAll();
        bool idle = WaitIdle();
        bool back = AtS0(t2);
        ui.Invoke(() => t2.PlaceVisible(vis0)); // undo the size change ourselves (physics never resizes)
        timer.Sleep(0.15);
        var off = windows.Where(w => !AtS0(w)).Select(w => w.Label).ToList();
        Check("drag path: restore returns the thrown window to where the drag started", idle && back && off.Count == 0,
            off.Count > 0 ? "not restored: " + string.Join(", ", off) : "");
    }

    private void ScenarioHungApp()
    {
        Console.WriteLine("-- stalled app: a window whose thread is blocked must not block the simulation");
        TestWindow? hung = null;
        var ready = new ManualResetEventSlim();
        var th = new Thread(() =>
        {
            hung = new TestWindow("Busy", "plane", Color.FromArgb(20, 184, 166), topMost: false);
            hung.Shown += (_, _) => ready.Set();
            hung.ShowAtVisible(RectD.FromXYWH(arena.L + 0.25 * arena.W, arena.T + 0.45 * arena.H, 280 * u, 180 * u));
            Application.Run(hung);
        }) { IsBackground = true, Name = "HungWindowThread" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        ready.Wait(3000);
        timer.Sleep(0.2);
        IntPtr hh = (IntPtr)hung!.Invoke(() => hung.Handle);
        source.Register(hh);
        everCreated.Add(hh);
        Native.GetWindowRect(hh, out var hr0);
        s0[hh] = new SavedPos(hr0.Left, hr0.Top);

        hung.BeginInvoke(() => Thread.Sleep(1500)); // the window's thread stops pumping messages
        timer.Sleep(0.05);
        long f0 = engine.Inspect((t, _) => t.ActiveFrames);
        var sw = Stopwatch.StartNew();
        engine.Fling(hh, 1500 * u, 0);
        engine.Fling(H(t1), 0, 1600 * u);
        double flingCall = sw.Elapsed.TotalMilliseconds;
        var p0 = Geo(t1).VisibleD;
        timer.Sleep(0.5);
        var p1 = Geo(t1).VisibleD;
        long f1 = engine.Inspect((t, _) => t.ActiveFrames);
        Check("stalled app: simulation keeps running and other windows keep moving",
            f1 - f0 > 20 && Math.Abs(p1.T - p0.T) > 20 && flingCall < 100,
            $"{f1 - f0} frames in 0.5 s while the window's thread was blocked; other window moved {F(p1.T - p0.T)} px");
        WaitIdle();
        timer.Sleep(1.2);
        WaitIdle();
        Native.GetWindowRect(hh, out var hr1);
        Check("stalled app: its queued async moves are applied once it wakes up", hr1.Left > hr0.Left + 50,
            $"moved {hr1.Left - hr0.Left} px after the stall");
        RestoreAndCheck("stalled window included");
        source.Unregister(hh);
        s0.Remove(hh);
        hung.BeginInvoke(() => hung.Close());
        th.Join(3000);
    }

    private void ScenarioGravity()
    {
        Console.WriteLine("-- gravity on: all test windows fall and pile up");
        int b0 = BounceCount;
        engine.SetGravity(true, (int)arena.CenterX, (int)arena.CenterY);
        Check("gravity: every window in the arena joins", windows.All(w => engine.KindOf(H(w)) == BodyKind.Gravity));
        bool settled = WaitIdle(10);
        Check("gravity: the pile settles and goes to sleep", settled && engine.GravityOn);
        var vis = windows.ToDictionary(w => w, w => Geo(w).VisibleD);
        bool OnFloor(RectD r) => Math.Abs(r.B - arena.B) <= 1;
        bool OnWindow(TestWindow w) => windows.Any(o => o != w && Math.Abs(vis[o].T - vis[w].B) <= 1 && vis[o].OverlapX(vis[w]) > 1);
        Check("gravity: every window rests on the floor or on another window",
            windows.All(w => OnFloor(vis[w]) || OnWindow(w)),
            string.Join(", ", windows.Select(w => $"{w.Label} bottom {vis[w].B:0}")));
        Check("gravity: 'Balloon' piled on 'Book' and 'Sofa' on 'Gift'",
            Math.Abs(vis[t1].B - vis[t2].T) <= 1 && Math.Abs(vis[t3].B - vis[t4].T) <= 1,
            $"gaps {vis[t2].T - vis[t1].B:0} and {vis[t4].T - vis[t3].B:0} px");
        int overlaps = 0;
        for (int i = 0; i < windows.Count; i++)
        for (int j = i + 1; j < windows.Count; j++)
            if (vis[windows[i]].IntersectArea(vis[windows[j]]) > vis[windows[i]].W) overlaps++;
        Check("gravity: stacked windows do not overlap", overlaps == 0, $"{overlaps} overlapping pair(s)");
        var gb = Geo(t2);
        Check("no gap: the visible frame lands flush on the taskbar edge, the invisible border hangs below",
            gb.Visible.Bottom == (int)arena.B && gb.Window.Bottom > arena.B,
            $"visible bottom {gb.Visible.Bottom}, window-rect bottom {gb.Window.Bottom}, work-area bottom {arena.B}");
        Check("gravity: windows bounced on landing", BouncesSince(b0).Any(b => !b.AxisX));
        Check("gravity: pile is inside the work area", windows.All(w => InsideArena(w, out _)));

        Console.WriteLine("-- gravity: lift 'Balloon' off the pile and let go slowly; it falls back");
        var rel = ScriptedDrag.Throw(ui, watcher, H(t1), 0, -180 * u, 0.3, timer);
        Check("gravity: a released window drops even when not thrown", rel != null && rel.Flung && engine.KindOf(H(t1)) == BodyKind.Gravity,
            rel?.Note ?? "no release");
        settled = WaitIdle(8);
        vis[t1] = Geo(t1).VisibleD;
        vis[t2] = Geo(t2).VisibleD;
        Check("gravity: it lands back on the pile", settled && Math.Abs(vis[t1].B - vis[t2].T) <= 1, $"gap {vis[t2].T - vis[t1].B:0} px");

        Console.WriteLine("-- gravity off: windows fly back to where they were");
        engine.SetGravity(false);
        bool idle = WaitIdle(6);
        var off = windows.Where(w => !AtS0(w)).Select(w => w.Label).ToList();
        Check("gravity off: every window animates back to its exact position", idle && off.Count == 0 && !engine.GravityOn,
            off.Count > 0 ? "not restored: " + string.Join(", ", off) : "all at original pixels");
    }

    private void ScenarioEligibility()
    {
        Console.WriteLine("-- eligibility: windows that must be skipped");
        var t5 = Spawn("Skip me", "gift", Color.FromArgb(100, 116, 139), RectD.FromXYWH(arena.L + 0.62 * arena.W, arena.T + 0.42 * arena.H, 260 * u, 170 * u));
        var t6 = Spawn("Tool", "gift", Color.FromArgb(100, 116, 139), RectD.FromXYWH(arena.L + 0.32 * arena.W, arena.T + 0.42 * arena.H, 260 * u, 170 * u), tool: true);
        IntPtr h5 = H(t5), h6 = H(t6);
        Check("eligibility: a normal test window is eligible", engine.Reject(h5) == null, engine.Reject(h5) ?? "ok");
        Check("eligibility: tool windows are skipped", engine.Reject(h6) == "tool window", engine.Reject(h6) ?? "eligible?!");

        ui.Invoke(() => Native.ShowWindow(h5, Native.SW_SHOWMINNOACTIVE));
        timer.Sleep(0.3);
        bool minFling = engine.Fling(h5, 2000, 0);
        Check("eligibility: minimized windows are skipped and cannot be thrown", engine.Reject(h5) == "minimized" && !minFling,
            engine.Reject(h5) ?? "eligible?!");
        ui.Invoke(() => Native.ShowWindow(h5, Native.SW_SHOWNOACTIVATE));
        timer.Sleep(0.3);

        // Real maximizing would activate the window and steal focus from the user, so set the
        // WS_MAXIMIZE state bit that IsZoomed() reports instead.
        const long WS_MAXIMIZE = 0x01000000;
        long style = Native.GetWindowLong(h5, Native.GWL_STYLE);
        SetWindowLongPtr(h5, Native.GWL_STYLE, new IntPtr(style | WS_MAXIMIZE));
        Check("eligibility: maximized windows are skipped", engine.Reject(h5) == "maximized", engine.Reject(h5) ?? "eligible?!");
        SetWindowLongPtr(h5, Native.GWL_STYLE, new IntPtr(style));

        Native.SetDwmInt(h5, Native.DWMWA_CLOAK, 1);
        timer.Sleep(0.1);
        Check("eligibility: cloaked windows are skipped", engine.Reject(h5) == "cloaked", engine.Reject(h5) ?? "eligible?!");
        Native.SetDwmInt(h5, Native.DWMWA_CLOAK, 0);

        bool moved = engine.Inspect((t, _) => t.MovedWindows.Contains(h5) || t.MovedWindows.Contains(h6));
        Check("eligibility: skipped windows were never moved", !moved);
        foreach (var w in new[] { t5, t6 })
        {
            source.Unregister(H(w));
            ui.Invoke(w.Close);
            windows.Remove(w);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private void ScenarioGrabbable()
    {
        Console.WriteLine("-- safety: a window whose title bar starts above the screen is pulled back when it moves");
        var g = Geo(t4).VisibleD;
        ui.Invoke(() => t4.PlaceVisible(RectD.FromXYWH(arena.L + 0.45 * arena.W, arena.T - 120 * u, g.W, g.H)));
        timer.Sleep(0.2);
        int fixes0 = engine.Inspect((t, _) => t.GrabbableFixes);
        engine.Fling(H(t4), 500 * u, 0);
        WaitIdle();
        var after = Geo(t4).VisibleD;
        int fixes = engine.Inspect((t, _) => t.GrabbableFixes) - fixes0;
        Check("safety: title bar is grabbable again after moving", after.T >= arena.T - 1 && fixes > 0,
            $"top {F(after.T)}, {fixes} fix(es)");
    }

    // ================================================================== helpers

    private void Cleanup()
    {
        try { if (watcher != null) ui.Invoke(watcher.Dispose); } catch { }
        try { engine?.Dispose(); } catch { }
        foreach (var w in windows.ToList())
        {
            try { ui.Invoke(w.Close); } catch { }
        }
        windows.Clear();
        timer.Dispose();
    }

    private static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static Dictionary<IntPtr, RECT> SnapshotUserWindows()
    {
        var d = new Dictionary<IntPtr, RECT>();
        uint own = (uint)Environment.ProcessId;
        foreach (var h in WindowFilter.TopLevelWindows())
        {
            if (!Native.IsWindowVisible(h)) continue;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == own) continue;
            if (Native.GetWindowRect(h, out var r)) d[h] = r;
        }
        return d;
    }
}
