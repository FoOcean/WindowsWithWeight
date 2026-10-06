namespace WindowsWithWeight;

/// <summary>Axis-aligned rectangle in physical pixels (double precision). Right/Bottom are exclusive.</summary>
public readonly record struct RectD(double L, double T, double R, double B)
{
    public double W => R - L;
    public double H => B - T;
    public double Area => Math.Max(0, W) * Math.Max(0, H);
    public double CenterX => (L + R) * 0.5;
    public double CenterY => (T + B) * 0.5;

    public static RectD FromXYWH(double x, double y, double w, double h) => new(x, y, x + w, y + h);
    public RectD Offset(double dx, double dy) => new(L + dx, T + dy, R + dx, B + dy);
    public RectD MoveTo(double x, double y) => new(x, y, x + W, y + H);

    public double OverlapX(in RectD o) => Math.Min(R, o.R) - Math.Max(L, o.L);
    public double OverlapY(in RectD o) => Math.Min(B, o.B) - Math.Max(T, o.T);
    public bool Intersects(in RectD o) => OverlapX(o) > 0 && OverlapY(o) > 0;

    public double IntersectArea(in RectD o)
    {
        double ox = OverlapX(o), oy = OverlapY(o);
        return ox > 0 && oy > 0 ? ox * oy : 0;
    }

    public bool Contains(double x, double y) => x >= L && x < R && y >= T && y < B;
    public bool Contains(in RectD o, double tol = 0) => o.L >= L - tol && o.T >= T - tol && o.R <= R + tol && o.B <= B + tol;

    public RECT ToRECT() => new((int)Math.Round(L), (int)Math.Round(T), (int)Math.Round(R), (int)Math.Round(B));
    public override string ToString() => $"[{L:0.#},{T:0.#} {W:0.#}x{H:0.#}]";
}

/// <summary>
/// The region windows are allowed to occupy: the union of monitor work areas.
/// Monitors never overlap in virtual-screen space, so the union area is a plain sum.
/// Walls are implicit: a move that increases the part of a window lying outside the union
/// is blocked. That handles multi-monitor layouts, taskbars between monitors and gaps
/// between monitors with the same code path, and lets windows that already hang partly
/// off-screen move back in (but never further out).
/// </summary>
public sealed class Bounds
{
    public IReadOnlyList<RectD> Areas { get; }

    public Bounds(IEnumerable<RectD> areas)
    {
        Areas = areas.Where(a => a.W > 0 && a.H > 0).ToArray();
    }

    public Bounds(RectD single) : this(new[] { single }) { }

    /// <summary>Area (px²) of <paramref name="r"/> outside every work area.</summary>
    public double Uncovered(in RectD r)
    {
        double covered = 0;
        foreach (var a in Areas) covered += r.IntersectArea(a);
        double u = r.Area - covered;
        return u > 0 ? u : 0;
    }

    public bool IsInside(in RectD r, double tolPx = 0.75) => Uncovered(r) <= tolPx * (r.W + r.H);

    /// <summary>The work area containing the rect's centre, or the one sharing most area with it.</summary>
    public RectD HomeArea(in RectD r)
    {
        RectD best = Areas[0];
        double bestScore = double.MinValue;
        foreach (var a in Areas)
        {
            double score = a.Contains(r.CenterX, r.CenterY) ? double.MaxValue / 2 : r.IntersectArea(a) - Distance(a, r.CenterX, r.CenterY);
            if (score > bestScore) { bestScore = score; best = a; }
        }
        return best;
    }

    private static double Distance(in RectD a, double x, double y)
    {
        double dx = Math.Max(Math.Max(a.L - x, 0), x - a.R);
        double dy = Math.Max(Math.Max(a.T - y, 0), y - a.B);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// True when a strip of the title bar (top <paramref name="stripH"/> px, at least
    /// <paramref name="minW"/> px wide) lies inside one work area, so the user can still grab it.
    /// </summary>
    public bool IsGrabbable(in RectD r, double stripH, double minW)
    {
        double need = Math.Min(minW, r.W);
        var strip = new RectD(r.L, r.T, r.R, r.T + Math.Min(stripH, r.H));
        foreach (var a in Areas)
        {
            if (strip.OverlapX(a) >= need - 0.5 && strip.OverlapY(a) >= strip.H - 0.5) return true;
        }
        return false;
    }

    /// <summary>Smallest translation that makes the title strip grabbable again.</summary>
    public RectD MakeGrabbable(in RectD r, double stripH, double minW)
    {
        if (IsGrabbable(r, stripH, minW)) return r;
        var a = HomeArea(r);
        double need = Math.Min(minW, r.W);
        double h = Math.Min(stripH, r.H);
        double dx = 0, dy = 0;
        if (r.T < a.T) dy = a.T - r.T;
        else if (r.T + h > a.B) dy = a.B - h - r.T;
        if (r.R < a.L + need) dx = a.L + need - r.R;
        else if (r.L > a.R - need) dx = a.R - need - r.L;
        return r.Offset(dx, dy);
    }

    public override string ToString() => string.Join(" + ", Areas);
}
