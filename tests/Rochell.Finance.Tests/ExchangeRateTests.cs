using System.Text.Json;
using Rochell.Finance.ExchangeRates;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>
/// USD1-02 (USD-01, E-USD-2, E-USD1-02-1…8): rates prepared by Tesorería and approved by the Controller, corrected by a new rate of the
/// same day, never for a future day, and the rate a document of a date takes — its own day, or on a weekend the last one before.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExchangeRateTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    /// <summary>The last Friday on or before <paramref name="date"/>.</summary>
    private static DateOnly LastFriday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7));

    private static async Task<(Guid Id, long Version)> PrepareAsync(TestHarness h, Guid session, string key, DateOnly date, decimal rate)
    {
        var result = await h.RunAsync(new PrepareExchangeRate(h.CompanyId, session, key, "USD", date, rate, "Banco Central — tasa de venta"), new PrepareExchangeRateHandler());
        return (result.ResultRef, 1);
    }

    private static async Task<string> ForAsync(TestHarness h, Guid session, DateOnly date)
    {
        var rate = JsonDocument.Parse(await h.QueryAsync(new GetExchangeRateForDate(h.CompanyId, session, date), new GetExchangeRateForDateHandler())).RootElement;
        return $"{rate.GetProperty("rateDate").GetString()}:{rate.GetProperty("rate").GetString()}";
    }

    [Trait("AcceptanceUsd1", "USD-01")]
    [Fact]
    public async Task USD01_a_rate_is_prepared_by_Tesoreria_approved_by_the_Controller_and_without_the_days_rate_none_applies()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var treasurer = await h.SessionWithRolesAsync("TESORERO");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var friday = LastFriday(Today(h).AddDays(-1));
        var (rate, _) = await PrepareAsync(h, treasurer, "r1", friday, 60.1234m);
        var own = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveExchangeRate(h.CompanyId, treasurer, "own", rate, 1), new ApproveExchangeRateHandler()));
        var before = await Assert.ThrowsAsync<DomainException>(() => ForAsync(h, controller, friday));
        await h.RunAsync(new ApproveExchangeRate(h.CompanyId, controller, "a1", rate, 1), new ApproveExchangeRateHandler());
        var future = await Assert.ThrowsAsync<DomainException>(() => PrepareAsync(h, treasurer, "future", Today(h).AddDays(1), 60m));
        var tooPrecise = await Assert.ThrowsAsync<DomainException>(() => PrepareAsync(h, treasurer, "precise", friday, 60.12345m));

        Assert.Equal(AuthorizationErrors.NotAuthorized, own.Code); // the Tesorero prepares and never approves (SoD)
        Assert.Equal(ExchangeRateErrors.Missing, before.Code);
        Assert.Equal((ExchangeRateErrors.Future, ExchangeRateErrors.Invalid), (future.Code, tooPrecise.Code));
        Assert.Equal($"{friday:yyyy-MM-dd}:60.1234", await ForAsync(h, controller, friday));
    }

    [Fact]
    public async Task A_weekend_takes_Fridays_rate_a_weekday_needs_its_own_and_a_correction_supersedes_the_rate_of_its_day()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var treasurer = await h.SessionWithRolesAsync("TESORERO");
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var friday = LastFriday(Today(h).AddDays(-7));
        var (first, _) = await PrepareAsync(h, treasurer, "r1", friday, 60.0000m);
        await h.RunAsync(new ApproveExchangeRate(h.CompanyId, controller, "a1", first, 1), new ApproveExchangeRateHandler());

        var saturday = await ForAsync(h, controller, friday.AddDays(1));
        var sunday = await ForAsync(h, controller, friday.AddDays(2));
        var monday = await Assert.ThrowsAsync<DomainException>(() => ForAsync(h, controller, friday.AddDays(3)));

        // The Contador corrects Friday's rate; once approved it supersedes the first.
        var (fix, _) = await PrepareAsync(h, contador, "r2", friday, 60.2500m);
        await h.RunAsync(new ApproveExchangeRate(h.CompanyId, controller, "a2", fix, 1), new ApproveExchangeRateHandler());
        var (wrong, _) = await PrepareAsync(h, treasurer, "r3", friday, 99m);
        await h.RunAsync(new DiscardExchangeRate(h.CompanyId, treasurer, "d3", wrong, 1), new DiscardExchangeRateHandler());
        var list = JsonDocument.Parse(await h.QueryAsync(new ListExchangeRates(h.CompanyId, await h.SessionWithRolesAsync("AUDITOR"), friday, friday), new ListExchangeRatesHandler()))
            .RootElement.GetProperty("items");

        Assert.Equal(($"{friday:yyyy-MM-dd}:60.0000", $"{friday:yyyy-MM-dd}:60.0000"), (saturday, sunday));
        Assert.Equal(ExchangeRateErrors.Missing, monday.Code);
        Assert.Equal($"{friday:yyyy-MM-dd}:60.2500", await ForAsync(h, controller, friday.AddDays(1)));
        Assert.Equal(
            "ACTIVE:60.2500,SUPERSEDED:60.0000,DISCARDED:99.0000",
            string.Join(',', list.EnumerateArray().Select(r => $"{r.GetProperty("status").GetString()}:{r.GetProperty("rate").GetString()}")
                .OrderBy(x => x.StartsWith("ACTIVE", StringComparison.Ordinal) ? 0 : x.StartsWith("SUPERSEDED", StringComparison.Ordinal) ? 1 : 2)));
        Assert.Equal(
            "ExchangeRate:null>DRAFT,DRAFT>ACTIVE,ACTIVE>SUPERSEDED",
            "ExchangeRate:" + await h.ScalarAsync<string>(
                $"SELECT string_agg(coalesce(from_state, 'null') || '>' || to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = '{first}'"));
    }
}
