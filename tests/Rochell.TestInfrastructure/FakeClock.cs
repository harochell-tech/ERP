using Rochell.Platform.Time;

namespace Rochell.TestInfrastructure;

/// <summary>Controllable clock. Starts at the real current time so database now() and the clock stay consistent.</summary>
public sealed class FakeClock : IClock
{
    private DateTime _now = SystemClock.Instance.UtcNow;

    public DateTime UtcNow => _now;

    public void Advance(TimeSpan by) => _now = Precision.ToMicroseconds(_now.Add(by));
}
