namespace WindowsWithWeight.Physics;

/// <summary>
/// Tunables. Distances are physical pixels at 100% scaling; every speed / acceleration is
/// multiplied by the monitor scale factor of the body, so the feel is the same at 150%.
/// </summary>
public sealed record PhysicsSettings
{
    public string Name { get; init; } = "Normal";
    /// <summary>Exponential friction rate for sliding windows (1/s). v *= exp(-Friction * dt).</summary>
    public double Friction { get; init; } = 1.7;
    /// <summary>Constant deceleration on top of the exponential part (px/s²) so windows really stop.</summary>
    public double Decel { get; init; } = 140;
    /// <summary>Bounciness for walls and window-window impacts.</summary>
    public double Restitution { get; init; } = 0.5;
    /// <summary>Gravity mode acceleration (px/s²).</summary>
    public double Gravity { get; init; } = 3600;
    /// <summary>Air drag in gravity mode (1/s).</summary>
    public double AirDrag { get; init; } = 0.25;
    /// <summary>Horizontal damping while resting on the floor / another window in gravity mode (1/s).</summary>
    public double GroundFriction { get; init; } = 6.0;
    /// <summary>Coulomb friction coefficient for sliding contact between two windows.</summary>
    public double ContactFriction { get; init; } = 0.35;
    /// <summary>Release speed (px/s) above which a drag turns into a throw.</summary>
    public double ThrowThreshold { get; init; } = 550;
    /// <summary>Speed cap (px/s).</summary>
    public double MaxSpeed { get; init; } = 9000;
    /// <summary>Below this speed a sliding window is considered stopped (px/s).</summary>
    public double StopSpeed { get; init; } = 14;
    /// <summary>Impacts slower than this do not bounce (avoids micro-jitter) (px/s).</summary>
    public double RestSpeed { get; init; } = 90;

    public static readonly PhysicsSettings Light = new()
    {
        Name = "Light", Friction = 1.0, Decel = 60, Restitution = 0.72, Gravity = 3000, ThrowThreshold = 450,
    };

    public static readonly PhysicsSettings Normal = new();

    public static readonly PhysicsSettings Heavy = new()
    {
        Name = "Heavy", Friction = 3.0, Decel = 320, Restitution = 0.28, Gravity = 4400, ThrowThreshold = 700,
    };

    public static PhysicsSettings ByName(string name) => name.ToLowerInvariant() switch
    {
        "light" => Light,
        "heavy" => Heavy,
        _ => Normal,
    };
}
