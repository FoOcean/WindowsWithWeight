namespace WindowsWithWeight;

/// <summary>Position history of one window during a user drag; estimates the release velocity.</summary>
public sealed class DragTracker
{
    public readonly IntPtr Hwnd;
    public readonly RECT StartWindow;
    public readonly double StartTime;
    private readonly object gate = new();
    private readonly List<(double T, double X, double Y)> samples = new();
    private readonly List<(double T, int W, int H)> sizes = new();

    public DragTracker(IntPtr hwnd, double now)
    {
        Hwnd = hwnd;
        WasMaximized = Native.IsZoomed(hwnd);
        Native.GetWindowRect(hwnd, out StartWindow);
        StartTime = now;
        Add(now, StartWindow);
    }

    /// <summary>The drag began on a maximized window (Windows restores it under the cursor).</summary>
    public readonly bool WasMaximized;

    /// <summary>Where Restore should put the window: where the drag started, unless that was the maximized rect.</summary>
    public SavedPos? RestorePos => WasMaximized ? null : new SavedPos(StartWindow.Left, StartWindow.Top);
    public int Count { get { lock (gate) return samples.Count; } }

    /// <summary>Samples the window's current position (no window messages involved, safe on any thread).</summary>
    public void AddSample(double t)
    {
        if (Native.GetWindowRect(Hwnd, out var r)) Add(t, r);
    }

    private void Add(double t, RECT r)
    {
        lock (gate)
        {
            samples.Add((t, r.Left, r.Top));
            sizes.Add((t, r.Width, r.Height));
        }
    }

    public void Add(double t, double x, double y)
    {
        lock (gate) samples.Add((t, x, y));
    }

    /// <summary>
    /// True when the window changed size near the end of the gesture: a resize drag, or Aero Snap
    /// resizing it at release. A size change early on (dragging a maximized window out of
    /// maximize) is fine.
    /// </summary>
    public bool ResizedNearEnd(RECT now, double window = 0.15)
    {
        lock (gate)
        {
            double end = sizes.Count > 0 ? sizes.Max(s => s.T) : 0;
            foreach (var s in sizes)
            {
                if (s.T < end - window) continue;
                if (Math.Abs(s.W - now.Width) > 2 || Math.Abs(s.H - now.Height) > 2) return true;
            }
            return false;
        }
    }

    public (double Vx, double Vy, int N) Estimate(double window)
    {
        lock (gate) return VelocityEstimator.Estimate(samples, window);
    }
}

public static class VelocityEstimator
{
    /// <summary>
    /// Average velocity over the last <paramref name="window"/> seconds before the final sample:
    /// (p(t_end) - p(t_end - window)) / window, with p linearly interpolated between samples.
    /// Samples arrive both from LOCATIONCHANGE events (every mouse move) and from the simulation
    /// thread (every frame), so a pause before release is seen as a pause and yields ~0.
    /// </summary>
    public static (double Vx, double Vy, int N) Estimate(IReadOnlyList<(double T, double X, double Y)> raw, double window)
    {
        if (raw.Count < 2) return (0, 0, raw.Count);
        var s = raw.OrderBy(p => p.T).ToList();
        var end = s[^1];
        double t0 = Math.Max(s[0].T, end.T - window);
        if (end.T - t0 < 0.010) return (0, 0, s.Count);
        var (x0, y0) = At(s, t0);
        int n = s.Count(p => p.T >= t0);
        double dt = end.T - t0;
        return ((end.X - x0) / dt, (end.Y - y0) / dt, n);
    }

    private static (double X, double Y) At(List<(double T, double X, double Y)> s, double t)
    {
        for (int i = s.Count - 1; i >= 0; i--)
        {
            if (s[i].T > t) continue;
            if (i == s.Count - 1) return (s[i].X, s[i].Y);
            var a = s[i]; var b = s[i + 1];
            double span = b.T - a.T;
            double f = span > 0 ? (t - a.T) / span : 1;
            return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
        }
        return (s[0].X, s[0].Y);
    }
}

/// <summary>
/// Watches user drags with SetWinEventHook (out-of-context, so it needs the message loop of the
/// thread that installs it):
///  * EVENT_SYSTEM_MOVESIZESTART → catch the window if it is flying, start tracking.
///  * EVENT_OBJECT_LOCATIONCHANGE (hooked only for the dragged window's process, only during a drag) → samples.
///  * EVENT_SYSTEM_MOVESIZEEND → estimate velocity, throw.
/// </summary>
public sealed class DragWatcher : IDisposable
{
    private readonly Engine engine;
    private readonly uint scopePid;
    private readonly WinEventDelegate proc; // keep the delegate alive while hooks exist
    private IntPtr moveHook, locHook;
    private DragTracker? active;

    public int MoveSizeEvents { get; private set; }
    public int LocationEvents { get; private set; }
    public DragTracker? Active => active;

    /// <param name="scopePid">0 = all processes except our own (live). Otherwise only that process (self-test).</param>
    public DragWatcher(Engine engine, uint scopePid = 0)
    {
        this.engine = engine;
        this.scopePid = scopePid;
        proc = OnEvent;
    }

    public bool Install()
    {
        uint flags = Native.WINEVENT_OUTOFCONTEXT | (scopePid == 0 ? Native.WINEVENT_SKIPOWNPROCESS : 0);
        moveHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, proc, scopePid, 0, flags);
        return moveHook != IntPtr.Zero;
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            switch (ev)
            {
                case Native.EVENT_SYSTEM_MOVESIZESTART:
                    MoveSizeEvents++;
                    HandleStart(hwnd);
                    break;
                case Native.EVENT_SYSTEM_MOVESIZEEND:
                    MoveSizeEvents++;
                    HandleEnd(hwnd);
                    break;
                case Native.EVENT_OBJECT_LOCATIONCHANGE:
                    if (idObject == Native.OBJID_WINDOW && idChild == Native.CHILDID_SELF && active != null && hwnd == active.Hwnd)
                    {
                        LocationEvents++;
                        active.AddSample(engine.Now);
                    }
                    break;
            }
        }
        catch
        {
            // never let an exception unwind into user32
        }
    }

    /// <summary>MOVESIZESTART handler (also called directly by the self-test, which cannot press the mouse).</summary>
    public void HandleStart(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return;
        engine.Catch(hwnd); // grabbing a flying window stops it immediately
        StopTracking();
        if (!engine.Enabled) return;
        string? why = engine.Reject(hwnd);
        if (why != null && why != "maximized") return; // a maximized window dragged by its title restores itself
        active = new DragTracker(hwnd, engine.Now);
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        locHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, proc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
        engine.BeginDrag(active);
    }

    /// <summary>MOVESIZEEND handler.</summary>
    public ReleaseResult? HandleEnd(IntPtr hwnd)
    {
        if (active == null || active.Hwnd != hwnd) return null;
        var t = active;
        StopTracking();
        return engine.Release(t);
    }

    private void StopTracking()
    {
        if (locHook != IntPtr.Zero) { Native.UnhookWinEvent(locHook); locHook = IntPtr.Zero; }
        if (active != null) engine.EndDrag(active);
        active = null;
    }

    public void Dispose()
    {
        StopTracking();
        if (moveHook != IntPtr.Zero) { Native.UnhookWinEvent(moveHook); moveHook = IntPtr.Zero; }
    }
}
