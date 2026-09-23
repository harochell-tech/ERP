using System.Text.Json;
using Rochell.Platform.Hashing;
using Rochell.Platform.Time;
using Xunit;

namespace Rochell.Platform.Tests;

/// <summary>
/// Golden vectors for canonical serialization v1. Expected values were produced by an independent
/// implementation of the same specification (Python), not by this code. A change here is a hash format change.
/// </summary>
public sealed class CanonicalHashTests
{
    private const string GoldenPayload = "{\"z\":1, \"a\":\"Bloque 6\\\" – ñ\", \"n\":[3,-2,0], \"b\":true, \"m\":null, \"amount\":\"1250.5000\", \"\\u00e9\":\"x\"}";

    [Fact]
    public void Json_canonical_form_matches_golden()
        => Assert.Equal(
            "{\"a\":\"Bloque 6\\\" – ñ\",\"amount\":\"1250.5000\",\"b\":true,\"m\":null,\"n\":[3,-2,0],\"z\":1,\"é\":\"x\"}",
            JsonCanonicalizer.Canonicalize(GoldenPayload));

    [Fact]
    public void Domain_event_row_hash_matches_golden()
    {
        var row = new DomainEventRow(
            Guid.Parse("0192b6a0-0000-7000-8000-000000000001"),
            Guid.Parse("0192b6a0-0000-7000-8000-00000000000c"),
            Guid.Parse("0192b6a0-0000-7000-8000-0000000000c1"),
            1,
            "Pinged",
            1,
            "Ping",
            Guid.Parse("0192b6a0-0000-7000-8000-0000000000a1"),
            1,
            1,
            new DateTime(2026, 9, 23, 3, 30, 0, DateTimeKind.Utc).AddTicks(1_234_560),
            new DateTime(2026, 9, 23, 3, 30, 1, DateTimeKind.Utc).AddTicks(10),
            new DateOnly(2026, 9, 22),
            Guid.Parse("0192b6a0-0000-7000-8000-0000000000e1"),
            Guid.Parse("0192b6a0-0000-7000-8000-0000000000f1"),
            null,
            GoldenPayload);

        Assert.Equal("af6631488d6a486e3d9e6b444015c14e443d5ce3c68ee67ec4fde21210ffa149", Convert.ToHexStringLower(row.ComputeRowHash()));
    }

    [Fact]
    public void Primitive_encoding_matches_golden()
    {
        var writer = new CanonicalWriter().Text("abc").Null().Numeric(-1250.5m, 4).Boolean(true).Date(new DateOnly(2026, 1, 31)).Int64(-9_007_199_254_740_991).Text(string.Empty);

        Assert.Equal(
            "00000003616263ffffffff0000000a2d313235302e3530303000000001740000000a323032362d30312d3331000000112d3930303731393932353437343039393100000000",
            Convert.ToHexStringLower(writer.ToArray()));
        Assert.Equal("7b972124640751941ecd392ce6d5c523868fc49969c19fd2eb47b68264afc4ad", Convert.ToHexStringLower(writer.Sha256()));
    }

    [Theory]
    [InlineData("{\"x\":1.5}")]
    [InlineData("{\"x\":1e3}")]
    [InlineData("{\"x\":1.0}")]
    [InlineData("{\"x\":9007199254740992}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"x\":\"\\u0000\"}")]
    [InlineData("{\"x\":01}")]
    public void Json_outside_the_allowed_subset_is_rejected(string json)
        => Assert.ThrowsAny<JsonException>(() => JsonCanonicalizer.Canonicalize(json));

    [Theory]
    [InlineData("{\"x\":-0}", "{\"x\":0}")]
    [InlineData("\"\\u001f\\u2028\"", "\"\\u001f\u2028\"")]
    [InlineData("{\"b\":{\"d\":1,\"c\":2},\"a\":[{\"y\":1,\"x\":2}]}", "{\"a\":[{\"x\":2,\"y\":1}],\"b\":{\"c\":2,\"d\":1}}")]
    public void Json_canonicalization_rules(string input, string expected)
        => Assert.Equal(expected, JsonCanonicalizer.Canonicalize(input));

    [Fact]
    public void Values_that_cannot_round_trip_through_postgresql_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new CanonicalWriter().Numeric(1.23456m, 4));
        Assert.Throws<ArgumentException>(() => new CanonicalWriter().Timestamp(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(5)));
        Assert.Throws<ArgumentException>(() => new CanonicalWriter().Timestamp(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void Clock_and_business_calendar()
    {
        Assert.Equal(0, Precision.ToMicroseconds(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(17)).Ticks % 10);
        Assert.Equal(new DateOnly(2026, 9, 22), BusinessCalendar.DefaultBusinessDate(new DateTime(2026, 9, 23, 3, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 9, 23), BusinessCalendar.DefaultBusinessDate(new DateTime(2026, 9, 23, 4, 0, 0, DateTimeKind.Utc)));
    }
}
