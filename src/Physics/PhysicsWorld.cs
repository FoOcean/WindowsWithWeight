namespace WindowsWithWeight.Physics;

/// <summary>
/// Fixed-timestep rigid-box simulation of windows. Pure math: no Win32 calls, so it can be
/// unit-tested. The <see cref="Engine"/> owns an instance and feeds it real window geometry.
///
/// Model:
///  * Bodies are axis-aligned boxes (the visible frame of a window); no rotation.
///  * Mass is proportional to window area, so small windows bounce off big ones and big ones shove small ones.
///  * Walls are the work-area union in <see cref="Body.Bounds"/>; restitution from <see cref="Settings"/>.
///  * Free bodies slide with exponential + constant friction until they stop.
///  * Gravity bodies fall, bounce, stack and go to sleep when supported and slow.
///  * Restore bodies are kinematic tweens that ignore collisions.
///  * Pairs that already overlap when a body starts moving are "ghosted" (they pass through each other)
///    until they separate; desktop windows overlap all the time and should not explode apart.
/// </summary>
public sealed class PhysicsWorld
{
    public const double Dt = 1.0 / 480.0;
    private const double WakeSpeed = 250;            // px/s: impacts that wake a sleeping body
    private const double SleepSpeed = 20;            // px/s
    private const double SleepDelay = 0.25;          // s
    private const double SoftSeparationSpeed = 700;  // px/s: how fast slightly-overlapping windows are pushed apart
    private const double MaxFreeAge = 30;            // s: safety net

    public PhysicsSettings Settings = PhysicsSettings.Normal;
    public Bounds FreeBounds;

    public readonly List<Body> Bodies = new();
    public readonly Dictionary<IntPtr, Obstacle> Obstacles = new();
    /// <summary>Bodies that came to rest or finished their restore tween during the last steps.
    /// The owner drains this list.</summary>
    public readonly List<Body> Finished = new();

    public Action<Body>? Woken;
    public Action<BounceEvent>? Bounced;
    public Action<CollisionEvent>? Collided;

    private readonly HashSet<(IntPtr, IntPtr)> ghosts = new();
    public long StepCount { get; private set; }

    public PhysicsWorld(Bounds freeBounds)
    {
        FreeBounds = freeBounds;
    }

    // ------------------------------------------------------------------ bookkeeping

    public Body? Find(IntPtr hwnd)
    {
        foreach (var b in Bodies) if (b.Hwnd == hwnd) return b;
        return null;
    }

    public bool AllAsleep
    {
        get
        {
            foreach (var b in Bodies) if (!(b.Kind == BodyKind.Gravity && b.Asleep)) return false;
            return true;
        }
    }

    /// <summary>Adds a body. When <paramref name="ghostOverlaps"/> is set, everything it overlaps
    /// right now is ignored until they separate.</summary>
    public void Add(Body body, bool ghostOverlaps)
    {
        var existing = Find(body.Hwnd);
        if (existing != null) Bodies.Remove(existing);
        Obstacles.Remove(body.Hwnd);
        if (ghostOverlaps) GhostCurrentOverlaps(body, null);
        Bodies.Add(body);
    }

    public bool Remove(IntPtr hwnd)
    {
        var b = Find(hwnd);
        if (b == null) return false;
        Bodies.Remove(b);
        return true;
    }

    public void AddGhost(IntPtr a, IntPtr b) { if (a != b) ghosts.Add(Key(a, b)); }
    public bool IsGhost(IntPtr a, IntPtr b) => ghosts.Count > 0 && ghosts.Contains(Key(a, b));
    public int GhostCount => ghosts.Count;

    private static (IntPtr, IntPtr) Key(IntPtr a, IntPtr b) => a.ToInt64() < b.ToInt64() ? (a, b) : (b, a);

    private void GhostCurrentOverlaps(Body body, Body? except)
    {
        var r = body.Rect;
        foreach (var c in Bodies)
            if (c != body && c != except && c.IsDynamic && c.Rect.Intersects(r)) AddGhost(body.Hwnd, c.Hwnd);
        foreach (var o in Obstacles.Values)
            if (o.Hwnd != body.Hwnd && o.Rect.Intersects(r)) AddGhost(body.Hwnd, o.Hwnd);
    }

    /// <summary>
    /// Gravity mode start: pairs that overlap a lot are ghosted (no room to stack them, they sink together);
    /// slightly overlapping pairs are kept and gently pushed apart so they can stack.
    /// </summary>
    public void GhostDeepOverlaps(IReadOnlyList<Body> set, double fraction)
    {
        for (int i = 0; i < set.Count; i++)
        for (int j = i + 1; j < set.Count; j++)
        {
            var a = set[i].Rect; var b = set[j].Rect;
            double ox = a.OverlapX(b), oy = a.OverlapY(b);
            if (ox <= 0 || oy <= 0) continue;
            bool axisX = ox < oy;
            double depth = axisX ? ox : oy;
            double size = axisX ? Math.Min(a.W, b.W) : Math.Min(a.H, b.H);
            if (depth > fraction * size) AddGhost(set[i].Hwnd, set[j].Hwnd);
        }
    }

    // ------------------------------------------------------------------ stepping

    public void Step(double dt)
    {
        StepCount++;
        var s = Settings;

        for (int i = 0; i < Bodies.Count; i++)
        {
            var b = Bodies[i];
            b.PrevX = b.X; b.PrevY = b.Y; b.Supported = false; b.Age += dt;
        }

        // 1. integrate + walls
        for (int i = 0; i < Bodies.Count; i++)
        {
            var b = Bodies[i];
            if (b.Kind == BodyKind.Restore) { StepTween(b, dt); continue; }
            if (b.Asleep) continue;
            double sc = b.Scale;
            if (b.Kind == BodyKind.Gravity)
            {
                b.VY += s.Gravity * sc * dt;
                double drag = Math.Exp(-s.AirDrag * dt);
                b.VX *= drag; b.VY *= drag;
            }
            else
            {
                double sp = b.Speed;
                if (sp > 0)
                {
                    double ns = sp * Math.Exp(-s.Friction * dt) - s.Decel * sc * dt;
                    double f = ns > 0 ? ns / sp : 0;
                    b.VX *= f; b.VY *= f;
                }
            }
            double max = s.MaxSpeed * sc, speed = b.Speed;
            if (speed > max) { b.VX *= max / speed; b.VY *= max / speed; }
            b.X += b.VX * dt;
            b.Y += b.VY * dt;
            ResolveWalls(b);
        }

        // 2. contacts (a few relaxation passes so stacks settle)
        for (int iter = 0; iter < 4; iter++)
        {
            bool any = false;
            for (int i = 0; i < Bodies.Count; i++)
            {
                var a = Bodies[i];
                if (!a.IsDynamic) continue;
                for (int j = i + 1; j < Bodies.Count; j++)
                {
                    var b = Bodies[j];
                    if (!b.IsDynamic) continue;
                    any |= ResolvePair(a, b, iter);
                }
            }

            // resting windows hit by a sliding window wake up and join the simulation
            if (Obstacles.Count > 0)
            {
                for (int i = 0; i < Bodies.Count; i++)
                {
                    var a = Bodies[i];
                    if (a.Kind != BodyKind.Free) continue;
                    var ar = a.Rect;
                    List<Obstacle>? hit = null;
                    foreach (var o in Obstacles.Values)
                        if (o.Rect.Intersects(ar) && !IsGhost(a.Hwnd, o.Hwnd)) (hit ??= new()).Add(o);
                    if (hit == null) continue;
                    foreach (var o in hit)
                    {
                        var nb = Promote(o, a);
                        any |= ResolvePair(a, nb, iter);
                    }
                }
            }

            if (!any) break;
            for (int i = 0; i < Bodies.Count; i++)
            {
                var b = Bodies[i];
                if (b.IsDynamic && !b.Asleep) ResolveWalls(b);
            }
        }

        // 3. ghost pairs end once the two windows no longer overlap
        if (ghosts.Count > 0) ghosts.RemoveWhere(p => !StillOverlapping(p));

        // 4. rest / sleep / finish
        for (int i = Bodies.Count - 1; i >= 0; i--)
        {
            var b = Bodies[i];
            switch (b.Kind)
            {
                case BodyKind.Restore:
                    if (b.TweenT >= b.TweenDelay + b.TweenDuration)
                    {
                        Bodies.RemoveAt(i);
                        Finished.Add(b);
                    }
                    break;

                case BodyKind.Free:
                    if (b.Speed < s.StopSpeed * b.Scale || b.Age > MaxFreeAge)
                    {
                        b.VX = b.VY = 0;
                        Bodies.RemoveAt(i);
                        Finished.Add(b);
                        Obstacles[b.Hwnd] = new Obstacle(b.Hwnd, b.Rect, b.InsetL, b.InsetT, b.Scale);
                    }
                    break;

                case BodyKind.Gravity:
                    if (!b.Asleep)
                    {
                        bool support = b.Supported || OnFloor(b);
                        if (support)
                        {
                            b.VX *= Math.Exp(-s.GroundFriction * dt);
                            if (Math.Abs(b.VX) < 2) b.VX = 0;
                        }
                        if (support && b.Speed < SleepSpeed * b.Scale)
                        {
                            b.SlowTime += dt;
                            if (b.SlowTime > SleepDelay) { b.Asleep = true; b.VX = b.VY = 0; }
                        }
                        else b.SlowTime = 0;
                    }
                    else if ((StepCount & 7) == 0 && !HasSupport(b))
                    {
                        b.Asleep = false;
                        b.SlowTime = 0;
                    }
                    break;
            }
        }
    }

    /// <summary>Re-checks support for sleeping gravity bodies (used while the loop idles).</summary>
    public bool WakeUnsupported()
    {
        bool woke = false;
        foreach (var b in Bodies)
        {
            if (b.Kind == BodyKind.Gravity && b.Asleep && !HasSupport(b))
            {
                b.Asleep = false; b.SlowTime = 0; woke = true;
            }
        }
        return woke;
    }

    public void WakeAll()
    {
        foreach (var b in Bodies) { b.Asleep = false; b.SlowTime = 0; }
    }

    private static void StepTween(Body b, double dt)
    {
        b.TweenT += dt;
        double t = (b.TweenT - b.TweenDelay) / b.TweenDuration;
        if (t <= 0) return;
        if (t >= 1) { b.CurWX = b.ToWX; b.CurWY = b.ToWY; return; }
        double e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease-in-out cubic
        b.CurWX = b.FromWX + (b.ToWX - b.FromWX) * e;
        b.CurWY = b.FromWY + (b.ToWY - b.FromWY) * e;
    }

    private Body Promote(Obstacle o, Body hitter)
    {
        Obstacles.Remove(o.Hwnd);
        var nb = new Body(o.Hwnd, o.Rect, o.InsetL, o.InsetT, o.Scale, FreeBounds, BodyKind.Free);
        GhostCurrentOverlaps(nb, hitter);
        Bodies.Add(nb);
        Woken?.Invoke(nb);
        return nb;
    }

    private RectD? RectOf(IntPtr h)
    {
        var b = Find(h);
        if (b != null) return b.IsDynamic ? b.Rect : null;
        return Obstacles.TryGetValue(h, out var o) ? o.Rect : null;
    }

    private bool StillOverlapping((IntPtr, IntPtr) p)
    {
        var a = RectOf(p.Item1);
        var b = RectOf(p.Item2);
        return a.HasValue && b.HasValue && a.Value.Intersects(b.Value);
    }

    // ------------------------------------------------------------------ walls

    private static RectD RectAt(Body b, double x, double y) => new(x, y, x + b.W, y + b.H);

    /// <summary>
    /// Moves the body back to the exact contact point with the work-area boundary (binary search
    /// on the step fraction, per axis) and reflects the velocity component with restitution.
    /// A move is blocked when it increases the window area lying outside the work-area union.
    /// </summary>
    private void ResolveWalls(Body b)
    {
        var bounds = b.Bounds;
        double u0 = bounds.Uncovered(b.PrevRect);
        const double tol = 0.02;
        if (bounds.Uncovered(b.Rect) <= u0 + tol) return;

        double px = b.PrevX, py = b.PrevY;
        double dx = b.X - px, dy = b.Y - py;
        double nx = b.X, ny = b.Y;

        if (dx != 0 && bounds.Uncovered(RectAt(b, nx, py)) > u0 + tol)
        {
            double f = Search(t => bounds.Uncovered(RectAt(b, px + t * dx, py)) <= u0 + tol);
            nx = px + f * dx;
            Bounce(b, true, Math.Sign(dx), dx > 0 ? nx + b.W : nx);
        }
        if (dy != 0 && bounds.Uncovered(RectAt(b, nx, ny)) > u0 + tol)
        {
            double f = Search(t => bounds.Uncovered(RectAt(b, nx, py + t * dy)) <= u0 + tol);
            ny = py + f * dy;
            Bounce(b, false, Math.Sign(dy), dy > 0 ? ny + b.H : ny);
        }
        b.X = nx; b.Y = ny;
        if (bounds.Uncovered(b.Rect) > u0 + tol) { b.X = px; b.Y = py; }
    }

    private static double Search(Func<double, bool> ok)
    {
        double lo = 0, hi = 1;
        for (int i = 0; i < 18; i++)
        {
            double mid = (lo + hi) * 0.5;
            if (ok(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    private void Bounce(Body b, bool axisX, int dir, double edge)
    {
        double v = axisX ? b.VX : b.VY;
        if (v == 0 || Math.Sign(v) != dir) return; // pushed into the wall by a contact, not moving into it
        double vout = -v * Settings.Restitution;
        double rest = Settings.RestSpeed * b.Scale;
        if (Math.Abs(vout) < rest) vout = 0;
        if (axisX) b.VX = vout; else b.VY = vout;
        if (Math.Abs(v) > rest)
        {
            b.WallBounces++;
            Bounced?.Invoke(new BounceEvent(b.Hwnd, axisX, v, vout, edge, 0));
        }
    }

    private bool OnFloor(Body b)
    {
        var r = b.Rect;
        return b.Bounds.Uncovered(r.Offset(0, 1.0)) > b.Bounds.Uncovered(r) + 0.05;
    }

    private bool HasSupport(Body b)
    {
        if (OnFloor(b)) return true;
        var r = b.Rect;
        foreach (var c in Bodies)
        {
            if (c == b || !c.IsDynamic || IsGhost(b.Hwnd, c.Hwnd)) continue;
            if (Math.Abs(c.Y - r.B) <= 1.0 && r.OverlapX(c.Rect) > 1) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ contacts

    private double InvMass(Body b, bool axisX, double dir)
    {
        if (b.Asleep || !b.IsDynamic) return 0;
        // A window pressed against a wall in the push direction behaves as immovable.
        var r = b.Rect;
        var shifted = axisX ? r.Offset(dir * 0.75, 0) : r.Offset(0, dir * 0.75);
        if (b.Bounds.Uncovered(shifted) > b.Bounds.Uncovered(r) + 0.05) return 0;
        return 1.0 / b.Mass;
    }

    private bool ResolvePair(Body a, Body b, int iter)
    {
        if (a.Asleep && b.Asleep) return false;
        var ra = a.Rect; var rb = b.Rect;
        double ox = ra.OverlapX(rb), oy = ra.OverlapY(rb);
        if (ox <= 0 || oy <= 0) return false;
        if (IsGhost(a.Hwnd, b.Hwnd)) return false;

        var pa = a.PrevRect; var pb = b.PrevRect;
        double pox = pa.OverlapX(pb), poy = pa.OverlapY(pb);
        bool newContact = pox <= 0 || poy <= 0;
        bool axisX;
        if (pox <= 0 && poy > 0) axisX = true;
        else if (poy <= 0 && pox > 0) axisX = false;
        else if (pox <= 0 && poy <= 0)
        {
            // diagonal approach: the axis whose gap closed last is the contact face
            double fx = -pox / Math.Max(1e-9, ox - pox);
            double fy = -poy / Math.Max(1e-9, oy - poy);
            axisX = fx >= fy;
        }
        else axisX = ox < oy; // overlapping since before (gravity soft separation)

        if (!newContact && iter > 0) return false;

        double n = axisX ? (ra.CenterX <= rb.CenterX ? 1 : -1) : (ra.CenterY <= rb.CenterY ? 1 : -1);
        double va = axisX ? a.VX : a.VY, vb = axisX ? b.VX : b.VY;
        double rel = (vb - va) * n; // < 0: approaching
        double sc = Math.Max(a.Scale, b.Scale);

        if (a.Asleep && rel < -WakeSpeed * sc) { a.Asleep = false; a.SlowTime = 0; }
        if (b.Asleep && rel < -WakeSpeed * sc) { b.Asleep = false; b.SlowTime = 0; }

        double ia = InvMass(a, axisX, -n), ib = InvMass(b, axisX, n);
        if (!axisX)
        {
            var upper = n > 0 ? a : b;
            upper.Supported = true;
        }
        if (ia + ib <= 0) return false;

        double depth = axisX ? ox : oy;
        double corr = newContact ? depth : Math.Min(depth, SoftSeparationSpeed * sc * Dt);
        double wa = ia / (ia + ib), wb = ib / (ia + ib);
        if (axisX) { a.X -= n * corr * wa; b.X += n * corr * wb; }
        else { a.Y -= n * corr * wa; b.Y += n * corr * wb; }

        if (rel < 0)
        {
            var s = Settings;
            double e = -rel > s.RestSpeed * sc ? s.Restitution : 0;
            double j = -(1 + e) * rel / (ia + ib);
            double vaN = va - j * ia * n, vbN = vb + j * ib * n;

            // Coulomb friction on the tangential axis
            double ta = axisX ? a.VY : a.VX, tb = axisX ? b.VY : b.VX;
            double jt = -(tb - ta) / (ia + ib);
            double lim = s.ContactFriction * j;
            jt = Math.Clamp(jt, -lim, lim);
            double taN = ta - jt * ia, tbN = tb + jt * ib;

            if (axisX) { a.VX = vaN; b.VX = vbN; a.VY = taN; b.VY = tbN; }
            else { a.VY = vaN; b.VY = vbN; a.VX = taN; b.VX = tbN; }

            if (-rel > s.RestSpeed * sc)
            {
                a.Hits++; b.Hits++;
                Collided?.Invoke(new CollisionEvent(a.Hwnd, b.Hwnd, axisX, a.Mass, b.Mass,
                    va, vb, vaN, vbN, ia == 0, ib == 0));
            }
        }
        return true;
    }
}
