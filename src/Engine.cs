using System.Diagnostics;
using WindowsWithWeight.Physics;

namespace WindowsWithWeight;

/// <summary>Which windows physics may touch.</summary>
public interface IWindowSource
{
    /// <summary>null when eligible, otherwise the reason it is skipped.</summary>
    string? Reject(IntPtr hwnd);
    IEnumerable<IntPtr> Eligible();
}

/// <summary>Every eligible top-level window of other apps on the desktop.</summary>
public sealed class LiveWindowSource : IWindowSource
{
    public string? Reject(IntPtr hwnd) => WindowFilter.Reject(hwnd, testMode: false);

    public IEnumerable<IntPtr> Eligible()
    {
        foreach (var h in WindowFilter.TopLevelWindows())
            if (WindowFilter.Reject(h, testMode: false) == null) yield return h;
    }
}

/// <summary>Only windows this process created for the self-test / demo. Guarantees tests never move the user's windows.</summary>
public sealed class TestWindowSource : IWindowSource
{
    private readonly object gate = new();
    private readonly List<IntPtr> windows = new();

    public void Register(IntPtr hwnd) { lock (gate) if (!windows.Contains(hwnd)) windows.Add(hwnd); }
    public void Unregister(IntPtr hwnd) { lock (gate) windows.Remove(hwnd); }
    public bool Contains(IntPtr hwnd) { lock (gate) return windows.Contains(hwnd); }
    public IntPtr[] All { get { lock (gate) return windows.ToArray(); } }

    public string? Reject(IntPtr hwnd) => Contains(hwnd) ? WindowFilter.Reject(hwnd, testMode: true) : "not a test window";

    public IEnumerable<IntPtr> Eligible() => All.Where(h => Reject(h) == null).ToList();
}

/// <summary>Where windows may go.</summary>
public interface IArena
{
    /// <summary>Walls for thrown windows (union of work areas).</summary>
    Bounds FreeBounds();
    /// <summary>Walls and monitor rectangle for gravity mode on the monitor containing the point.</summary>
    (Bounds Walls, RectD Monitor) GravityArea(int x, int y);
    double ScaleAt(in RectD r);
}

public sealed class LiveArena : IArena
{
    public Bounds FreeBounds() => new(Monitors.All().Select(m => m.Work));

    public (Bounds Walls, RectD Monitor) GravityArea(int x, int y)
    {
        var m = Monitors.FromPoint(x, y);
        return (new Bounds(m.Work), m.Bounds);
    }

    public double ScaleAt(in RectD r) => Monitors.ScaleAt(r);
}

/// <summary>A single fixed rectangle (self-test arena or the demo stage).</summary>
public sealed class FixedArena : IArena
{
    public RectD Area { get; }
    public FixedArena(RectD area) { Area = area; }
    public Bounds FreeBounds() => new(Area);
    public (Bounds Walls, RectD Monitor) GravityArea(int x, int y) => (new Bounds(Area), Area);
    public double ScaleAt(in RectD r) => Monitors.ScaleAt(r);
}

/// <summary>Window-rect top-left (what SetWindowPos takes).</summary>
public readonly record struct SavedPos(int X, int Y);

public sealed record ReleaseResult(IntPtr Hwnd, double Vx, double Vy, int Samples, bool Flung, string Note)
{
    public double Speed => Math.Sqrt(Vx * Vx + Vy * Vy);
}

public sealed class Telemetry
{
    public readonly HashSet<IntPtr> MovedWindows = new();
    public long MoveCalls, MoveFailures;
    public readonly List<BounceEvent> Bounces = new();
    public readonly List<CollisionEvent> Collisions = new();
    public readonly List<ReleaseResult> Releases = new();
    public readonly List<(IntPtr Hwnd, double Vx, double Vy, BodyKind Kind)> Flings = new();
    public readonly Dictionary<IntPtr, string> Rejections = new();
    public int Catches, Dropped, GrabbableFixes, RestoresCompleted;
    public long ActiveFrames;
    public double ActiveTime;
    public double MaxOutsidePx;

    public double Fps => ActiveTime > 0 ? ActiveFrames / ActiveTime : 0;

    public void ResetFrameStats() { ActiveFrames = 0; ActiveTime = 0; }
}

/// <summary>
/// Runs the simulation on its own thread, paced by DwmFlush (display refresh) with a fixed
/// physics timestep, and moves real windows with asynchronous SetWindowPos so a hung app can
/// never stall the loop. All public members are thread-safe.
/// </summary>
public sealed class Engine : IDisposable
{
    public const double VelocityWindow = 0.070; // s of drag history used for the release velocity

    private readonly object gate = new();
    private readonly PhysicsWorld world;
    private readonly IWindowSource source;
    private readonly IArena arena;
    private readonly AutoResetEvent wake = new(false);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<IntPtr, SavedPos> originals = new();
    private readonly Dictionary<IntPtr, SavedPos> gravitySnap = new();
    private Thread? thread;
    private volatile bool stopping;
    private bool enabled = true;
    private bool gravityOn;
    private Bounds? gravityWalls;
    private RectD gravityMonitor;
    private DragTracker? drag;
    private double lastObstacleRefresh = -10, lastBoundsRefresh, lastSizeRefresh;
    private readonly double framePeriod;

    public Telemetry Telemetry { get; } = new();
    /// <summary>Raised (on the simulation or caller thread) when Enabled / Gravity / records change.</summary>
    public event Action? StateChanged;

    public Engine(IWindowSource source, IArena arena, PhysicsSettings settings)
    {
        this.source = source;
        this.arena = arena;
        world = new PhysicsWorld(arena.FreeBounds()) { Settings = settings };
        world.Woken = OnWoken;
        world.Bounced = e => Telemetry.Bounces.Add(e);
        world.Collided = e => Telemetry.Collisions.Add(e);
        framePeriod = 1.0 / Monitors.MaxRefreshHz();
    }

    public double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Live mode: a release with the cursor on a monitor edge is left to Aero Snap.
    /// Scripted drags (self-test / demo) turn this off because the user's real cursor can be anywhere.</summary>
    public bool IgnoreReleasesInSnapZones { get; set; } = true;
    public double FrameRateHint => 1.0 / framePeriod;
    public IWindowSource Source => source;
    public IArena Arena => arena;

    public void Start()
    {
        if (thread != null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "WindowsWithWeight.Simulation", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    // ================================================================== public API

    public bool Enabled
    {
        get { lock (gate) return enabled; }
        set
        {
            bool restore;
            lock (gate)
            {
                if (enabled == value) return;
                enabled = value;
                restore = !value && gravityOn;
                if (!value) StopAllLocked();
            }
            if (restore) SetGravity(false, force: true);
            Changed();
        }
    }

    public PhysicsSettings Settings
    {
        get { lock (gate) return world.Settings; }
        set { lock (gate) world.Settings = value; Changed(); }
    }

    public bool GravityOn { get { lock (gate) return gravityOn; } }
    public int SavedPositionCount { get { lock (gate) return originals.Count; } }
    public bool IsSimulating(IntPtr hwnd) { lock (gate) return world.Find(hwnd) != null; }
    public BodyKind? KindOf(IntPtr hwnd) { lock (gate) return world.Find(hwnd)?.Kind; }
    public bool HasSavedPosition(IntPtr hwnd) { lock (gate) return originals.ContainsKey(hwnd); }

    /// <summary>True while anything is moving (sleeping gravity piles do not count).</summary>
    public bool IsBusy { get { lock (gate) return drag != null || (world.Bodies.Count > 0 && !world.AllAsleep); } }

    /// <summary>Runs <paramref name="f"/> under the engine lock (telemetry / body inspection for tests).</summary>
    public T Inspect<T>(Func<Telemetry, PhysicsWorld, T> f) { lock (gate) return f(Telemetry, world); }

    /// <summary>Eligibility as seen by this engine (live: other apps' windows; test: own test windows).</summary>
    public string? Reject(IntPtr hwnd) => source.Reject(hwnd);

    /// <summary>
    /// Starts momentum for a window: the same entry point a real drag release uses.
    /// <paramref name="origin"/> is the position recorded for "Restore positions" (defaults to the current one).
    /// </summary>
    public bool Fling(IntPtr hwnd, double vx, double vy, SavedPos? origin = null)
    {
        string? why = source.Reject(hwnd);
        if (why != null)
        {
            lock (gate) Telemetry.Rejections[hwnd] = why;
            return false;
        }
        if (!WindowGeometry.TryGet(hwnd, out var g)) return false;
        var vis = g.VisibleD;
        double scale = arena.ScaleAt(vis);
        lock (gate)
        {
            if (!enabled) return false;
            var pos = origin ?? new SavedPos(g.Window.Left, g.Window.Top);
            originals.TryAdd(hwnd, pos);
            bool grav = gravityOn && gravityMonitor.Contains(vis.CenterX, vis.CenterY);
            if (grav) gravitySnap.TryAdd(hwnd, pos);
            else RefreshObstaclesLocked(force: true);
            var b = new Body(hwnd, vis, g.InsetL, g.InsetT, scale, grav ? gravityWalls! : world.FreeBounds,
                grav ? BodyKind.Gravity : BodyKind.Free)
            {
                VX = vx, VY = vy,
            };
            world.Add(b, ghostOverlaps: true);
            Telemetry.Flings.Add((hwnd, vx, vy, b.Kind));
        }
        wake.Set();
        Changed();
        return true;
    }

    /// <summary>The user grabbed the window (MOVESIZESTART): stop simulating it immediately.</summary>
    public bool Catch(IntPtr hwnd)
    {
        lock (gate)
        {
            world.Obstacles.Remove(hwnd);
            if (!world.Remove(hwnd)) return false;
            Telemetry.Catches++;
            if (gravityOn) world.WakeAll(); // something may have been resting on it
            return true;
        }
    }

    /// <summary>Drag started: the simulation thread samples the window every frame too.</summary>
    public void BeginDrag(DragTracker t)
    {
        lock (gate) drag = t;
        wake.Set();
    }

    /// <summary>Stops per-frame sampling for a drag that ended without a release (or was abandoned).</summary>
    public void EndDrag(DragTracker t)
    {
        lock (gate) { if (drag == t) drag = null; }
    }

    /// <summary>Drag ended: estimate the release velocity from the last ~70 ms and throw if fast enough.</summary>
    public ReleaseResult Release(DragTracker t)
    {
        double now = Now;
        t.AddSample(now);
        lock (gate) { if (drag == t) drag = null; }
        var (vx, vy, n) = t.Estimate(VelocityWindow);
        ReleaseResult Result(bool flung, string note)
        {
            var r = new ReleaseResult(t.Hwnd, vx, vy, n, flung, note);
            lock (gate) Telemetry.Releases.Add(r);
            return r;
        }

        if (!WindowGeometry.TryGet(t.Hwnd, out var g)) return Result(false, "window gone");
        if (t.ResizedNearEnd(g.Window)) return Result(false, "resized or snapped");
        if (Native.IsZoomed(t.Hwnd) || Native.IsIconic(t.Hwnd)) return Result(false, "maximized/minimized");
        if (IgnoreReleasesInSnapZones && Native.GetCursorPos(out var cp))
        {
            // Released against a screen edge: Windows is about to snap it. Do not fight Aero Snap.
            var m = Monitors.FromPoint(cp.X, cp.Y).Bounds;
            if (cp.X <= m.L + 1 || cp.X >= m.R - 2 || cp.Y <= m.T + 1) return Result(false, "released in a snap zone");
        }
        var vis = g.VisibleD;
        double scale = arena.ScaleAt(vis);
        double speed = Math.Sqrt(vx * vx + vy * vy);
        bool inGravity, on;
        PhysicsSettings s;
        lock (gate)
        {
            on = enabled;
            inGravity = gravityOn && gravityMonitor.Contains(vis.CenterX, vis.CenterY);
            s = world.Settings;
        }
        if (!on) return Result(false, "disabled");
        if (speed < s.ThrowThreshold * scale && !inGravity) return Result(false, "below throw threshold");
        bool ok = Fling(t.Hwnd, vx, vy, t.RestorePos);
        return Result(ok, ok ? (inGravity ? "dropped into gravity" : "thrown") : "not eligible");
    }

    public bool ToggleGravity() => SetGravity(!GravityOn);

    /// <summary>
    /// Gravity on: every eligible window on the monitor under the cursor (or at x,y) falls and piles up.
    /// Gravity off: they animate back to where they were when gravity was switched on.
    /// </summary>
    public bool SetGravity(bool on, int? x = null, int? y = null, bool force = false)
    {
        lock (gate)
        {
            if (on == gravityOn) return gravityOn;
            if (on)
            {
                if (!enabled && !force) return false;
                int px, py;
                if (x.HasValue && y.HasValue) { px = x.Value; py = y.Value; }
                else { Native.GetCursorPos(out var cp); px = cp.X; py = cp.Y; }
                var (walls, monitor) = arena.GravityArea(px, py);
                gravityOn = true;
                gravityWalls = walls;
                gravityMonitor = monitor;
                var work = walls.Areas[0];
                var set = new List<Body>();
                foreach (var h in source.Eligible())
                {
                    if (!WindowGeometry.TryGet(h, out var g)) continue;
                    var vis = g.VisibleD;
                    if (!monitor.Contains(vis.CenterX, vis.CenterY)) continue;
                    if (vis.W > work.W || vis.H > work.H) continue; // cannot fit, leave it alone
                    var pos = new SavedPos(g.Window.Left, g.Window.Top);
                    var existing = world.Find(h);
                    if (existing != null && existing.Kind == BodyKind.Restore)
                    {
                        world.Remove(h);
                        existing = null;
                    }
                    if (existing != null)
                    {
                        // keep its momentum, but the snapshot is where its window is drawn right now
                        pos = new SavedPos((int)Math.Round(existing.X) - existing.InsetL, (int)Math.Round(existing.Y) - existing.InsetT);
                    }
                    gravitySnap[h] = pos;
                    originals.TryAdd(h, pos);
                    var b = existing ?? new Body(h, vis, g.InsetL, g.InsetT, arena.ScaleAt(vis), walls, BodyKind.Gravity);
                    b.Kind = BodyKind.Gravity;
                    b.Bounds = walls;
                    b.Asleep = false;
                    if (existing == null) world.Add(b, ghostOverlaps: false);
                    world.Obstacles.Remove(h);
                    set.Add(b);
                }
                world.GhostDeepOverlaps(set, 0.35);
            }
            else
            {
                gravityOn = false;
                BeginRestoreLocked(new Dictionary<IntPtr, SavedPos>(gravitySnap), stopOthers: false);
                foreach (var b in world.Bodies)
                {
                    if (b.Kind == BodyKind.Gravity)
                    {
                        b.Kind = BodyKind.Free;
                        b.Bounds = world.FreeBounds;
                        b.Asleep = false;
                    }
                }
                gravitySnap.Clear();
            }
        }
        wake.Set();
        Changed();
        return on;
    }

    /// <summary>Animates every window this app moved back to where it was before the app first moved it.</summary>
    public void RestoreAll()
    {
        lock (gate)
        {
            gravityOn = false;
            gravitySnap.Clear();
            BeginRestoreLocked(new Dictionary<IntPtr, SavedPos>(originals), stopOthers: true);
        }
        wake.Set();
        Changed();
    }

    /// <summary>Freezes everything where it is (positions stay recorded).</summary>
    public void StopAll()
    {
        lock (gate) StopAllLocked();
        Changed();
    }

    /// <summary>Stops the loop. Windows still in gravity mode or mid-restore are put back instantly.</summary>
    public void Shutdown()
    {
        lock (gate)
        {
            foreach (var b in world.Bodies)
                if (b.Kind == BodyKind.Restore) Move(b.Hwnd, b.TargetWX, b.TargetWY);
            if (gravityOn)
            {
                foreach (var (h, p) in gravitySnap)
                    if (Native.IsWindow(h) && !Native.IsIconic(h) && !Native.IsZoomed(h)) Move(h, p.X, p.Y);
                gravityOn = false;
                gravitySnap.Clear();
            }
            world.Bodies.Clear();
            drag = null;
        }
        stopping = true;
        wake.Set();
        thread?.Join(1500);
    }

    public void Dispose()
    {
        if (!stopping) Shutdown();
        wake.Dispose();
    }

    // ================================================================== internals (call with gate held)

    private void Changed() => StateChanged?.Invoke();

    private void StopAllLocked()
    {
        for (int i = world.Bodies.Count - 1; i >= 0; i--)
        {
            var b = world.Bodies[i];
            if (b.Kind == BodyKind.Restore) continue;
            world.Bodies.RemoveAt(i);
        }
    }

    private void BeginRestoreLocked(Dictionary<IntPtr, SavedPos> targets, bool stopOthers)
    {
        var items = new List<(IntPtr H, SavedPos To, int Fx, int Fy, double Top)>();
        foreach (var (h, to) in targets)
        {
            if (!Native.IsWindow(h) || Native.IsIconic(h) || Native.IsZoomed(h))
            {
                originals.Remove(h);
                world.Remove(h);
                continue;
            }
            var b = world.Find(h);
            int fx, fy;
            if (b != null && b.IsDynamic) { fx = (int)Math.Round(b.X) - b.InsetL; fy = (int)Math.Round(b.Y) - b.InsetT; }
            else if (b != null) { fx = (int)Math.Round(b.CurWX); fy = (int)Math.Round(b.CurWY); }
            else if (Native.GetWindowRect(h, out var wr)) { fx = wr.Left; fy = wr.Top; }
            else continue;
            world.Remove(h);
            world.Obstacles.Remove(h);
            if (fx == to.X && fy == to.Y)
            {
                if (originals.TryGetValue(h, out var o) && o == to) originals.Remove(h);
                continue;
            }
            items.Add((h, to, fx, fy, fy));
        }
        if (stopOthers) StopAllLocked();

        int i = 0;
        foreach (var it in items.OrderBy(t => t.Top))
        {
            double dist = Math.Sqrt(Math.Pow(it.To.X - it.Fx, 2) + Math.Pow(it.To.Y - it.Fy, 2));
            double dur = Math.Clamp(0.35 + dist / 2600.0, 0.45, 0.9);
            var rb = Body.CreateRestore(it.H, it.Fx, it.Fy, it.To.X, it.To.Y, i * 0.045, dur, world.FreeBounds);
            rb.LastAppliedX = it.Fx; rb.LastAppliedY = it.Fy;
            world.Add(rb, ghostOverlaps: false);
            i++;
        }
    }

    private void OnWoken(Body nb)
    {
        // a resting window got hit: remember where it was so Restore can put it back
        if (Native.GetWindowRect(nb.Hwnd, out var wr)) originals.TryAdd(nb.Hwnd, new SavedPos(wr.Left, wr.Top));
        nb.Bounds = world.FreeBounds;
    }

    private void RefreshObstaclesLocked(bool force)
    {
        double now = Now;
        if (!force && now - lastObstacleRefresh < 0.3) return;
        lastObstacleRefresh = now;
        var seen = new HashSet<IntPtr>();
        foreach (var h in source.Eligible())
        {
            if (world.Find(h) != null) continue;
            if (!WindowGeometry.TryGet(h, out var g)) continue;
            var vis = g.VisibleD;
            seen.Add(h);
            if (world.Obstacles.TryGetValue(h, out var o))
            {
                o.Rect = vis; o.InsetL = g.InsetL; o.InsetT = g.InsetT;
            }
            else world.Obstacles[h] = new Obstacle(h, vis, g.InsetL, g.InsetT, arena.ScaleAt(vis));
        }
        foreach (var h in world.Obstacles.Keys.Where(k => !seen.Contains(k)).ToList()) world.Obstacles.Remove(h);
    }

    private void Move(IntPtr h, int x, int y)
    {
        const uint flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE |
                           Native.SWP_ASYNCWINDOWPOS | Native.SWP_NOOWNERZORDER;
        Telemetry.MoveCalls++;
        Telemetry.MovedWindows.Add(h);
        if (!Native.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, flags))
        {
            Telemetry.MoveFailures++;
            world.Remove(h);
            Native.GetWindowThreadProcessId(h, out uint pid);
            WindowFilter.ForgetProcess(pid);
        }
    }

    /// <summary>Per-frame housekeeping: drop windows that closed / minimized / maximized, follow size
    /// changes (e.g. DPI change when crossing monitors), refresh walls and resting windows.</summary>
    private void PeriodicLocked(double now)
    {
        for (int i = world.Bodies.Count - 1; i >= 0; i--)
        {
            var b = world.Bodies[i];
            if (!Native.IsWindow(b.Hwnd) || Native.IsIconic(b.Hwnd) || Native.IsZoomed(b.Hwnd))
            {
                world.Bodies.RemoveAt(i);
                Telemetry.Dropped++;
            }
        }

        if (now - lastSizeRefresh > 0.25)
        {
            lastSizeRefresh = now;
            foreach (var b in world.Bodies)
            {
                if (!b.IsDynamic || !WindowGeometry.TryGet(b.Hwnd, out var g)) continue;
                if (Math.Abs(g.Visible.Width - b.W) > 1 || Math.Abs(g.Visible.Height - b.H) > 1)
                {
                    b.W = g.Visible.Width; b.H = g.Visible.Height;
                    b.InsetL = g.InsetL; b.InsetT = g.InsetT;
                }
                if (!b.Bounds.IsGrabbable(b.Rect, 32 * b.Scale, 120 * b.Scale))
                {
                    var r = b.Bounds.MakeGrabbable(b.Rect, 32 * b.Scale, 120 * b.Scale);
                    b.X = b.PrevX = r.L; b.Y = b.PrevY = r.T;
                    Telemetry.GrabbableFixes++;
                }
            }
        }

        if (now - lastBoundsRefresh > 1.0)
        {
            lastBoundsRefresh = now;
            var fb = arena.FreeBounds();
            if (fb.Areas.Count > 0)
            {
                world.FreeBounds = fb;
                foreach (var b in world.Bodies) if (b.Kind == BodyKind.Free) b.Bounds = fb;
            }
        }

        if (world.Bodies.Any(b => b.Kind == BodyKind.Free)) RefreshObstaclesLocked(force: false);
    }

    private void ApplyLocked(double alpha)
    {
        foreach (var b in world.Bodies)
        {
            int wx, wy;
            if (b.Kind == BodyKind.Restore)
            {
                if (b.TweenT < b.TweenDelay) continue;
                wx = (int)Math.Round(b.CurWX);
                wy = (int)Math.Round(b.CurWY);
            }
            else
            {
                double rx = b.PrevX + (b.X - b.PrevX) * alpha;
                double ry = b.PrevY + (b.Y - b.PrevY) * alpha;
                wx = (int)Math.Round(rx) - b.InsetL;
                wy = (int)Math.Round(ry) - b.InsetT;
                double outside = b.Bounds.Uncovered(b.Rect) / Math.Max(1, b.W + b.H);
                if (outside > Telemetry.MaxOutsidePx) Telemetry.MaxOutsidePx = outside;
            }
            if (wx == b.LastAppliedX && wy == b.LastAppliedY) continue;
            b.LastAppliedX = wx; b.LastAppliedY = wy;
            Move(b.Hwnd, wx, wy);
        }
    }

    private void DrainFinishedLocked()
    {
        if (world.Finished.Count == 0) return;
        bool changed = false;
        foreach (var b in world.Finished)
        {
            if (world.Find(b.Hwnd) != null) continue; // woken again in the same frame
            if (b.Kind == BodyKind.Restore)
            {
                Move(b.Hwnd, b.TargetWX, b.TargetWY);
                if (originals.TryGetValue(b.Hwnd, out var o) && o.X == b.TargetWX && o.Y == b.TargetWY)
                {
                    originals.Remove(b.Hwnd);
                    changed = true;
                }
                Telemetry.RestoresCompleted++;
            }
            else
            {
                int wx = (int)Math.Round(b.X) - b.InsetL, wy = (int)Math.Round(b.Y) - b.InsetT;
                if (wx != b.LastAppliedX || wy != b.LastAppliedY) Move(b.Hwnd, wx, wy);
            }
        }
        world.Finished.Clear();
        if (changed) ThreadPool.QueueUserWorkItem(_ => Changed());
    }

    // ================================================================== loop

    private void Run()
    {
        using var timer = new PreciseTimer();
        double acc = 0, last = Now, lastFrame = Now;
        bool idle = true;
        while (!stopping)
        {
            bool busy, pile;
            lock (gate)
            {
                busy = drag != null || (world.Bodies.Count > 0 && !world.AllAsleep);
                pile = !busy && world.Bodies.Count > 0;
            }
            if (!busy)
            {
                // Nothing moves. A sleeping gravity pile only needs an occasional support check.
                wake.WaitOne(pile ? 100 : Timeout.Infinite);
                if (stopping) break;
                if (pile)
                {
                    lock (gate)
                    {
                        PeriodicLocked(Now);
                        world.WakeUnsupported();
                    }
                }
                idle = true;
                continue;
            }
            if (idle) { last = Now; acc = 0; idle = false; }

            // Pace to the display: DwmFlush blocks until the next composition. If it returns
            // immediately (nothing to compose) fall back to a high-resolution timer.
            double t0 = Now;
            int hr = Native.DwmFlush();
            if (hr != 0 || Now - t0 < 0.0008)
            {
                double wait = lastFrame + framePeriod - Now;
                if (wait > 0) timer.Sleep(wait);
            }
            lastFrame = Now;

            double now = Now;
            double frame = Math.Min(now - last, 0.05);
            last = now;
            acc += frame;

            lock (gate)
            {
                if (drag != null && (now - drag.StartTime > 300 || !Native.IsWindow(drag.Hwnd))) drag = null; // abandoned drag
                drag?.AddSample(now);
                PeriodicLocked(now);
                int steps = 0;
                while (acc >= PhysicsWorld.Dt && steps < 32)
                {
                    world.Step(PhysicsWorld.Dt);
                    acc -= PhysicsWorld.Dt;
                    steps++;
                }
                if (steps >= 32) acc = 0;
                DrainFinishedLocked();
                ApplyLocked(acc / PhysicsWorld.Dt);
                if (world.Bodies.Count > 0)
                {
                    Telemetry.ActiveFrames++;
                    Telemetry.ActiveTime += frame;
                }
            }
        }
    }
}
