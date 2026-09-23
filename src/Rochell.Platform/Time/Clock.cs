namespace Rochell.Platform.Time;

/// <summary>UTC clock with PostgreSQL precision (microseconds), so hashed timestamps round-trip exactly.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTime UtcNow => Precision.ToMicroseconds(DateTime.UtcNow);
}

public static class Precision
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>Truncates to microseconds and forces UTC. Rejects non-UTC values to avoid silent offset bugs.</summary>
    public static DateTime ToMicroseconds(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamps must be UTC (DateTimeKind.Utc).", nameof(value));
        }

        return new DateTime(value.Ticks - (value.Ticks % TicksPerMicrosecond), DateTimeKind.Utc);
    }
}

/// <summary>
/// Default business date: calendar date in America/Santo_Domingo (UTC-4, no DST).
/// Shift-based business dates (ADR-023) are supplied explicitly by manufacturing commands in later PRs.
/// </summary>
public static class BusinessCalendar
{
    private static readonly TimeZoneInfo DominicanRepublic = TimeZoneInfo.FindSystemTimeZoneById("America/Santo_Domingo");

    public static DateOnly DefaultBusinessDate(DateTime utc)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Precision.ToMicroseconds(utc), DominicanRepublic));
}
