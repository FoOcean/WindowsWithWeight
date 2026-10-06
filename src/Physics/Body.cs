namespace WindowsWithWeight.Physics;

public enum BodyKind
{
    /// <summary>Thrown or pushed window sliding on the "desk" with friction.</summary>
    Free,
    /// <summary>Window under gravity (gravity mode).</summary>
    Gravity,
    /// <summary>Kinematic tween back to a saved position; ignores collisions.</summary>
    Restore,
}

/// <summary>A simulated window. Position is the top-left of the *visible* frame.</summary>
public sealed class Body
{
    public readonly IntPtr Hwnd;
    public BodyKind Kind;

    // visible frame, physical px
    public double X, Y, W, H;
    public double PrevX, PrevY;
    public double VX, VY;

    /// <summary>Visible frame minus window rect (the invisible resize border on Win10/11).</summary>
    public int InsetL, InsetT;

    /// <summary>DPI scale of the monitor this body lives on (1.0 = 96 dpi).</summary>
    public double Scale = 1.0;
    public Bounds Bounds;

    public bool Asleep;
    public double SlowTime;
    public bool Supported;
    public double Age;

    // Restore tween (window-rect coordinates)
    public double FromWX, FromWY, ToWX, ToWY, CurWX, CurWY;
    public int TargetWX, TargetWY;
    public double TweenT, TweenDelay, TweenDuration;

    // Rendering bookkeeping
    public int LastAppliedX = int.MinValue, LastAppliedY = int.MinValue;
    public int StepsSinceRefresh;

    // Telemetry
    public int WallBounces;
    public int Hits;

    public Body(IntPtr hwnd, RectD visible, int insetL, int insetT, double scale, Bounds bounds, BodyKind kind)
    {
        Hwnd = hwnd;
        X = PrevX = visible.L;
        Y = PrevY = visible.T;
        W = visible.W;
        H = visible.H;
        InsetL = insetL;
        InsetT = insetT;
        Scale = scale;
        Bounds = bounds;
        Kind = kind;
    }

    public static Body CreateRestore(IntPtr hwnd, int fromWX, int fromWY, int toWX, int toWY, double delay, double duration, Bounds bounds)
    {
        var b = new Body(hwnd, new RectD(fromWX, fromWY, fromWX + 1, fromWY + 1), 0, 0, 1, bounds, BodyKind.Restore)
        {
            FromWX = fromWX, FromWY = fromWY, CurWX = fromWX, CurWY = fromWY,
            ToWX = toWX, ToWY = toWY, TargetWX = toWX, TargetWY = toWY,
            TweenDelay = delay, TweenDuration = duration,
        };
        return b;
    }

    public RectD Rect => new(X, Y, X + W, Y + H);
    public RectD PrevRect => new(PrevX, PrevY, PrevX + W, PrevY + H);
    public double Mass => Math.Max(1.0, W * H);
    public double Speed => Math.Sqrt(VX * VX + VY * VY);
    public bool IsDynamic => Kind != BodyKind.Restore;

    public override string ToString() => $"{Kind} 0x{Hwnd.ToInt64():X} {Rect} v=({VX:0},{VY:0}){(Asleep ? " asleep" : "")}";
}

/// <summary>A resting window that is not simulated but can be hit (and then woken up).</summary>
public sealed class Obstacle
{
    public readonly IntPtr Hwnd;
    public RectD Rect;
    public int InsetL, InsetT;
    public double Scale;

    public Obstacle(IntPtr hwnd, RectD rect, int insetL, int insetT, double scale)
    {
        Hwnd = hwnd; Rect = rect; InsetL = insetL; InsetT = insetT; Scale = scale;
    }
}

public readonly record struct BounceEvent(IntPtr Hwnd, bool AxisX, double SpeedIn, double SpeedOut, double Edge, double WallCoord);

public readonly record struct CollisionEvent(
    IntPtr A, IntPtr B, bool AxisX, double MassA, double MassB,
    double VaBefore, double VbBefore, double VaAfter, double VbAfter,
    bool AStatic, bool BStatic);
