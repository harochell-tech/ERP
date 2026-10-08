using System.Text.Json;
using System.Text.Json.Nodes;
using Rochell.Identity;
using Rochell.Identity.Authorization;
using Rochell.Platform.Commands;
using Rochell.Tax.Ecf;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Tax.Tests;

/// <summary>
/// VS4-02 (E-VS4-1…6, E-VS4-02-1…7): the e-NCF ranges and the gateway's queue against the simulated Alanube — numbering, the status
/// schedule, the unknown outcome resolved without a second e-NCF, contingency and its end, the 24-hour limit, the files, the webhook nudge.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EcfGatewayTests(PostgresFixture postgres)
{
    private static readonly Guid Daily = IdentityConstants.DailyProcessUserId;

    [Fact]
    public async Task A_range_is_prepared_by_one_and_approved_by_another_and_replaces_the_active_one()
    {
        var w = await World.CreateAsync(postgres);
        var h = w.H;

        var first = await w.ActiveSeriesAsync("31", 1, 100);
        var draft = await w.PrepareAsync("31", 101, 200, w.Specialist, "p2");
        var self = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveEcfSeries(h.CompanyId, w.Specialist, "a-self", draft, 1), new ApproveEcfSeriesHandler()));
        var overlap = await Assert.ThrowsAsync<DomainException>(() => w.PrepareAsync("31", 150, 300, w.Specialist, "p3"));
        var approved = Json(await h.RunAsync(new ApproveEcfSeries(h.CompanyId, w.Controller, "a2", draft, 1), new ApproveEcfSeriesHandler()));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListEcfSeries(h.CompanyId, w.Specialist), new ListEcfSeriesHandler())).RootElement.GetProperty("items");

        Assert.Equal(AuthorizationErrors.NotAuthorized, self.Code); // the preparer never holds ecf_series:approve (SoD)
        Assert.Equal(EcfErrors.SeriesInvalid, overlap.Code);
        Assert.Equal(first.ToString(), approved.GetProperty("closedSeriesId").GetString());
        Assert.Equal("CLOSED", await h.ScalarAsync<string>("SELECT status FROM tax.ecf_series WHERE series_id = @s", ("s", first)));
        var active = list.EnumerateArray().Single(i => i.GetProperty("status").GetString() == "ACTIVE");
        Assert.Equal("E310000000101", active.GetProperty("from").GetString());
        Assert.Equal("E310000000101", active.GetProperty("next").GetString());
        Assert.Equal(100, active.GetProperty("remaining").GetInt64());
    }

    [Trait("AcceptanceVs4", "ECF-09")]
    [Fact]
    public async Task Each_issuance_takes_the_next_eNCF_and_the_last_one_closes_the_range()
    {
        var w = await World.CreateAsync(postgres);

        var missing = await Assert.ThrowsAsync<DomainException>(() => w.QueueAsync("31", "q0"));
        await w.ActiveSeriesAsync("31", 41, 42);
        var a = await w.QueueAsync("31", "q1");
        var b = await w.QueueAsync("31", "q2");
        var exhausted = await Assert.ThrowsAsync<DomainException>(() => w.QueueAsync("31", "q3"));

        Assert.Equal(EcfErrors.SeriesMissing, missing.Code);
        Assert.Equal("E310000000041", a.Encf);
        Assert.Equal("E310000000042", b.Encf);
        Assert.Equal(EcfErrors.SeriesMissing, exhausted.Code);
        Assert.Equal("CLOSED", await w.H.ScalarAsync<string>("SELECT status FROM tax.ecf_series WHERE company_id = @c", ("c", w.H.CompanyId)));
        Assert.Equal("PENDING", await w.StatusAsync(a.DocumentId));
    }

    [Fact]
    public async Task An_expired_range_refuses_the_issuance()
    {
        var w = await World.CreateAsync(postgres);
        await w.ActiveSeriesAsync("31", 1, 10, new DateOnly(2020, 12, 31));

        var ex = await Assert.ThrowsAsync<DomainException>(() => w.QueueAsync("31", "q1"));

        Assert.Equal(EcfErrors.SeriesExpired, ex.Code);
    }

    [Fact]
    public async Task An_accepted_eCF_keeps_its_security_code_and_signed_files_and_tells_its_invoice()
    {
        var w = await World.CreateAsync(postgres);
        await w.ActiveSeriesAsync("31", 1, 10);
        var doc = await w.QueueAsync("31", "q1");

        var sent = await w.AdvanceAsync(doc.DocumentId);
        var early = await w.DueAsync();
        w.Clock.Advance(TimeSpan.FromSeconds(11));
        var due = await w.DueAsync();
        var answered = await w.AdvanceAsync(doc.DocumentId);

        Assert.Equal("SUBMITTED", sent);
        Assert.DoesNotContain(doc.DocumentId, early);
        Assert.Contains(doc.DocumentId, due);
        Assert.StartsWith("ACCEPTED; files stored", answered);
        Assert.Equal("ACCEPTED", await w.StatusAsync(doc.DocumentId));
        Assert.Equal(2L, await w.H.ScalarAsync<long>("SELECT count(*) FROM tax.ecf_file WHERE document_id = @d", ("d", doc.DocumentId)));
        Assert.NotNull(await w.H.ScalarAsync<string>("SELECT security_code FROM tax.ecf_document WHERE document_id = @d", ("d", doc.DocumentId)));
        var told = Assert.Single(w.Updater.Seen);
        Assert.Equal(("ACCEPTED", doc.Encf), (told.Status, told.Encf));
        Assert.Equal(
            "SUBMITTED>ACCEPTED,PENDING>SUBMITTED",
            await w.H.ScalarAsync<string>(
                "SELECT string_agg(from_state || '>' || to_state, ',' ORDER BY to_state) FROM core.state_history WHERE aggregate_id = @d AND from_state IS NOT NULL",
                ("d", doc.DocumentId)));
    }

    [Fact]
    public async Task A_rejection_keeps_the_DGII_reason_and_fetches_no_files()
    {
        var w = await World.CreateAsync(postgres);
        await w.ActiveSeriesAsync("31", 1, 10);
        var doc = await w.QueueAsync("31", "q1", "[SIM:REJECT]");

        await w.AdvanceAsync(doc.DocumentId);
        w.Clock.Advance(TimeSpan.FromSeconds(11));
        var answered = await w.AdvanceAsync(doc.DocumentId);

        Assert.Equal("REJECTED", answered);
        Assert.StartsWith("DGII: Simulado", await w.H.ScalarAsync<string>("SELECT reason FROM tax.ecf_document WHERE document_id = @d", ("d", doc.DocumentId)));
        Assert.Equal(0L, await w.H.ScalarAsync<long>("SELECT count(*) FROM tax.ecf_file WHERE document_id = @d", ("d", doc.DocumentId)));
        Assert.Equal("REJECTED", Assert.Single(w.Updater.Seen).Status);
        Assert.DoesNotContain(doc.DocumentId, await w.DueAsync());
    }

    [Trait("AcceptanceVs4", "ECF-06")]
    [Fact]
    public async Task An_unknown_outcome_is_resolved_with_the_same_eNCF_and_never_a_second_one()
    {
        var w = await World.CreateAsync(postgres);
        await w.ActiveSeriesAsync("31", 1, 10);
        var doc = await w.QueueAsync("31", "q1", "[SIM:TIMEOUT]");

        var first = await w.AdvanceAsync(doc.DocumentId);
        w.Clock.Advance(TimeSpan.FromSeconds(11));
        var again = await w.AdvanceAsync(doc.DocumentId);
        w.Clock.Advance(TimeSpan.FromSeconds(11));
        var answered = await w.AdvanceAsync(doc.DocumentId);

        Assert.Equal("UNKNOWN_OUTCOME", first);
        Assert.Equal("SUBMITTED", again);
        Assert.StartsWith("ACCEPTED", answered);
        Assert.Equal(2, w.Provider.Submissions);
        Assert.Equal(2L, await w.H.ScalarAsync<long>("SELECT next_number FROM tax.ecf_series WHERE company_id = @c", ("c", w.H.CompanyId)));
        Assert.Equal(
            "SUBMIT:TIMEOUT,SUBMIT:REJECTED:AP3011,QUERY:OK,QUERY:OK", // the last: fresh links for the signed files
            await w.H.ScalarAsync<string>(
                "SELECT string_agg(operation || ':' || outcome || CASE WHEN outcome = 'REJECTED' THEN ':' || provider_code ELSE '' END, ',' ORDER BY called_at, call_id) FROM tax.ecf_call WHERE document_id = @d AND operation <> 'DOWNLOAD'",
                ("d", doc.DocumentId)));
    }

    [Fact]
    public async Task Content_Alanube_refuses_needs_attention()
    {
        var w = await World.CreateAsync(postgres);
        await w.ActiveSeriesAsync("31", 1, 10);
        var doc = await w.QueueAsync("31", "q1", "[SIM:INVALID]");

        var outcome = await w.AdvanceAsync(doc.DocumentId);

        Assert.Equal("REQUIRES_ACTION", outcome);
        Assert.Contains("AP10067", await w.H.ScalarAsync<string>("SELECT reason FROM tax.ecf_document WHERE document_id = @d", ("d", doc.DocumentId)));
        Assert.Equal("REQUIRES_ACTION", Assert.Single(w.Updater.Seen).Status);
    }

    [Trait("AcceptanceVs4", "ECF-08")]
    [Fact]
    public async Task Without_answers_past_the_policy_minutes_the_queue_goes_into_contingency_and_comes_back_by_itself()
    {
        var w = await World.CreateAsync(postgres);
        await w.H.CreateActivePolicyAsync("REVENUE_ACCOUNTING", new Dictionary<string, string> { ["ecf_contingency_minutes"] = "30" });
        await w.ActiveSeriesAsync("31", 1, 10);
        var a = await w.QueueAsync("31", "q1");
        var b = await w.QueueAsync("31", "q2");
        w.Provider.Down = true;

        var failed = await w.AdvanceAsync(a.DocumentId);
        w.Clock.Advance(TimeSpan.FromMinutes(31));
        var contingency = await w.AdvanceAsync(a.DocumentId);
        var statusB = await w.StatusAsync(b.DocumentId);
        w.Provider.Down = false;
        w.Clock.Advance(TimeSpan.FromMinutes(16));
        var back = await w.AdvanceAsync(a.DocumentId);

        Assert.Equal("UNKNOWN_OUTCOME", failed);
        Assert.Equal("CONTINGENCY", contingency);
        Assert.Equal("CONTINGENCY", statusB);
        Assert.Equal("SUBMITTED", back);
        Assert.Equal("PENDING", await w.StatusAsync(b.DocumentId));
        Assert.Contains(b.DocumentId, await w.DueAsync());
        Assert.False(await w.H.ScalarAsync<bool>("SELECT in_contingency FROM tax.ecf_gateway_state WHERE company_id = @c", ("c", w.H.CompanyId)));
        Assert.Empty(w.Updater.Seen);
    }

    [Fact]
    public async Task Without_the_policy_value_failures_never_become_contingency()
    {
        var w = await World.CreateAsync(postgres);
        await w.ActiveSeriesAsync("31", 1, 10);
        var a = await w.QueueAsync("31", "q1");
        w.Provider.Down = true;

        await w.AdvanceAsync(a.DocumentId);
        w.Clock.Advance(TimeSpan.FromHours(2));
        var outcome = await w.AdvanceAsync(a.DocumentId);

        Assert.Equal("UNKNOWN_OUTCOME", outcome.Split(',')[0]);
        Assert.Equal(2, await w.H.ScalarAsync<int>("SELECT consecutive_failures FROM tax.ecf_gateway_state WHERE company_id = @c", ("c", w.H.CompanyId)));
    }

    [Fact]
    public async Task The_DGII_slow_answer_follows_the_schedule_and_24_hours_without_one_needs_attention()
    {
        var w = await World.CreateAsync(postgres);
        await w.ActiveSeriesAsync("32", 1, 10);
        var doc = await w.QueueAsync("32", "q1", "[SIM:SLOW]");
        var slow = await w.QueueAsync("32", "q2", "[SIM:SLOW]");

        await w.AdvanceAsync(doc.DocumentId);
        w.Clock.Advance(TimeSpan.FromSeconds(11));
        await w.AdvanceAsync(doc.DocumentId);
        var next = await w.H.ScalarAsync<DateTime>("SELECT next_poll_at FROM tax.ecf_document WHERE document_id = @d", ("d", doc.DocumentId));
        await w.AdvanceAsync(slow.DocumentId);
        w.Clock.Advance(TimeSpan.FromHours(25));
        var late = await w.AdvanceAsync(slow.DocumentId);

        Assert.InRange(next - w.Clock.UtcNow.AddHours(-25), TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(31));
        Assert.Equal("REQUIRES_ACTION", late);
        Assert.Contains("24 horas", await w.H.ScalarAsync<string>("SELECT reason FROM tax.ecf_document WHERE document_id = @d", ("d", slow.DocumentId)));
    }

    [Fact]
    public async Task A_webhook_nudge_brings_the_status_query_forward_and_only_the_daily_process_may_run_the_gateway()
    {
        var w = await World.CreateAsync(postgres);
        await w.ActiveSeriesAsync("31", 1, 10);
        var doc = await w.QueueAsync("31", "q1");
        await w.AdvanceAsync(doc.DocumentId);
        var providerId = await w.H.ScalarAsync<string>("SELECT provider_id FROM tax.ecf_document WHERE document_id = @d", ("d", doc.DocumentId));

        var before = await w.DueAsync();
        var nudged = Json(await w.H.RunAsync(new NudgeEcfDocuments(w.H.CompanyId, await w.ServiceAsync(), "n1", providerId), new NudgeEcfDocumentsHandler(w.Provider)));
        var after = await w.DueAsync();
        var denied = await Assert.ThrowsAsync<DomainException>(
            () => w.H.RunAsync(new AdvanceEcfDocument(w.H.CompanyId, w.Specialist, "x", doc.DocumentId), new AdvanceEcfDocumentHandler(w.Provider, [w.Updater])));

        Assert.DoesNotContain(doc.DocumentId, before);
        Assert.Equal(1, nudged.GetProperty("nudged").GetInt32());
        Assert.Contains(doc.DocumentId, after);
        Assert.Equal(AuthorizationErrors.NotAuthorized, denied.Code);
    }

    [Fact]
    public async Task An_eCF_needing_attention_is_resolved_as_held_by_Alanube_or_as_never_issued()
    {
        var w = await World.CreateAsync(postgres);
        var h = w.H;
        await w.ActiveSeriesAsync("31", 1, 10);
        var held = await w.QueueAsync("31", "q1", "[SIM:INVALID]");
        var lost = await w.QueueAsync("31", "q2", "[SIM:INVALID]");
        await w.AdvanceAsync(held.DocumentId);
        await w.AdvanceAsync(lost.DocumentId);

        var shortNote = await Assert.ThrowsAsync<DomainException>(() => w.ResolveAsync(held.DocumentId, EcfResolutions.InAlanube, "01ABC", "corto"));
        var inAlanube = await w.ResolveAsync(held.DocumentId, EcfResolutions.InAlanube, "01abcdefghij", "Aparece en el portal de Alanube como registrado");
        var notIssued = await w.ResolveAsync(lost.DocumentId, EcfResolutions.NotIssued, null, "No aparece en Alanube ni en la DGII");
        var again = await Assert.ThrowsAsync<DomainException>(() => w.ResolveAsync(lost.DocumentId, EcfResolutions.NotIssued, null, "Segunda vez sobre el mismo e-CF"));

        Assert.Equal(EcfErrors.ResolutionInvalid, shortNote.Code);
        Assert.Equal("SUBMITTED", inAlanube.GetProperty("status").GetString());
        Assert.Equal("01ABCDEFGHIJ", await h.ScalarAsync<string>("SELECT provider_id FROM tax.ecf_document WHERE document_id = @d", ("d", held.DocumentId)));
        Assert.Contains(held.DocumentId, await w.DueAsync());
        Assert.Equal("REJECTED", notIssued.GetProperty("status").GetString());
        Assert.StartsWith(EcfResolutions.NotIssuedPrefix, await h.ScalarAsync<string>("SELECT reason FROM tax.ecf_document WHERE document_id = @d", ("d", lost.DocumentId)));
        Assert.Equal(EcfErrors.InvalidState, again.Code);
        Assert.Equal("REQUIRES_ACTION,REQUIRES_ACTION,REJECTED", string.Join(',', w.Updater.Seen.Select(x => x.Status)));
    }

    [Fact]
    public async Task The_inbox_lists_by_status_shows_the_calls_and_files_and_Inicio_counts_attention_and_ranges_running_out()
    {
        var w = await World.CreateAsync(postgres);
        var h = w.H;
        await h.CreateActivePolicyAsync("REVENUE_ACCOUNTING", new Dictionary<string, string> { ["ecf_range_alert_pct"] = "0.5", ["ecf_range_alert_days"] = "30" });
        await w.ActiveSeriesAsync("31", 41, 42);
        var accepted = await w.QueueAsync("31", "q1");
        await w.AdvanceAsync(accepted.DocumentId);
        await w.AdvanceAsync(accepted.DocumentId);
        await w.ActiveSeriesAsync("32", 1, 100);
        var invalid = await w.QueueAsync("32", "q2", "[SIM:INVALID]");
        await w.AdvanceAsync(invalid.DocumentId);

        var attention = JsonDocument.Parse(await h.QueryAsync(new ListEcfDocuments(h.CompanyId, w.Specialist, "REQUIRES_ACTION"), new ListEcfDocumentsHandler())).RootElement;
        var all = JsonDocument.Parse(await h.QueryAsync(new ListEcfDocuments(h.CompanyId, w.Specialist, Search: "E31"), new ListEcfDocumentsHandler())).RootElement;
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetEcfDocument(h.CompanyId, w.Specialist, accepted.DocumentId), new GetEcfDocumentHandler())).RootElement;
        var xml = detail.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "XML").GetProperty("fileId").GetGuid();
        var file = JsonDocument.Parse(await h.QueryAsync(new GetEcfFile(h.CompanyId, w.Specialist, xml), new GetEcfFileHandler())).RootElement;
        var alerts = JsonDocument.Parse(await h.QueryAsync(new GetEcfAlerts(h.CompanyId, w.Specialist), new GetEcfAlertsHandler())).RootElement;

        Assert.Equal((1, invalid.Encf), (attention.GetProperty("total").GetInt32(), attention.GetProperty("items")[0].GetProperty("encf").GetString()));
        Assert.Equal((1, "ACCEPTED"), (all.GetProperty("total").GetInt32(), all.GetProperty("items")[0].GetProperty("status").GetString()));
        // Calls of the same instant (the fake clock) come in any order.
        Assert.Equal("DOWNLOAD,DOWNLOAD,QUERY,QUERY,SUBMIT", string.Join(',', detail.GetProperty("calls").EnumerateArray().Select(c => c.GetProperty("operation").GetString()).Order()));
        Assert.NotNull(detail.GetProperty("securityCode").GetString());
        Assert.Equal(("E310000000041.xml", "simulated https://simulated.invalid/E310000000041.xml"), (file.GetProperty("fileName").GetString(),
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("contentBase64").GetString()!))));
        Assert.Equal((1, 0, false), (alerts.GetProperty("requiresAction").GetInt32(), alerts.GetProperty("rejected").GetInt32(), alerts.GetProperty("inContingency").GetBoolean()));
        var range = Assert.Single(alerts.GetProperty("ranges").EnumerateArray());
        Assert.Equal(("31", 1L, true), (range.GetProperty("ecfType").GetString(), range.GetProperty("remaining").GetInt64(), range.GetProperty("low").GetBoolean()));
    }

    [Trait("AcceptanceVs4", "ECF-10")]
    [Fact]
    public async Task ECF10_the_Controller_annuls_through_Alanube_the_unused_tail_of_a_closed_range_and_the_numbers_never_issued()
    {
        var w = await World.CreateAsync(postgres);
        var h = w.H;
        var series = await w.ActiveSeriesAsync("31", 1, 10);
        await w.QueueAsync("31", "q1");
        var lost = await w.QueueAsync("31", "q2", "[SIM:INVALID]");
        await w.AdvanceAsync(lost.DocumentId);
        await w.ResolveAsync(lost.DocumentId, EcfResolutions.NotIssued, null, "No aparece en Alanube ni en la DGII");
        var version = await h.ScalarAsync<long>("SELECT version FROM tax.ecf_series WHERE series_id = @s", ("s", series));
        var active = await Assert.ThrowsAsync<DomainException>(
            () => h.RunAsync(new CancelUnusedEcfNumbers(h.CompanyId, w.Controller, "c0", series, version), new CancelUnusedEcfNumbersHandler(w.Provider)));
        await h.RunAsync(new CloseEcfSeries(h.CompanyId, w.Controller, "close", series, version), new CloseEcfSeriesHandler());

        var cancelled = Json(await h.RunAsync(new CancelUnusedEcfNumbers(h.CompanyId, w.Controller, "c1", series, version + 1), new CancelUnusedEcfNumbersHandler(w.Provider)));

        Assert.Equal(EcfErrors.InvalidState, active.Code);
        Assert.Equal(("CANCELLED", 9), (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("quantity").GetInt32()));
        var sent = Assert.Single(w.Provider.Cancellations);
        Assert.Equal(9, sent["header"]!["cancelledEncfQuantity"]!.GetValue<long>());
        Assert.Equal(
            "E310000000002-E310000000002,E310000000003-E310000000010",
            string.Join(',', sent["cancellations"]![0]!["rangeCancelledEnfc"]!.AsArray().Select(r => $"{r!["encfFrom"]}-{r["encfUntil"]}")));
        Assert.Equal("3:10", await h.ScalarAsync<string>("SELECT cancelled_from || ':' || cancelled_to FROM tax.ecf_series WHERE series_id = @s", ("s", series)));
        Assert.Equal("CANCEL", await h.ScalarAsync<string>("SELECT operation FROM tax.ecf_call WHERE series_id = @s", ("s", series)));
    }

    private static JsonElement Json(CommandResult result) => JsonDocument.Parse(result.ResultPayload).RootElement;

    private sealed class World
    {
        private int _keys;

        public required TestHarness H { get; init; }

        public required FakeClock Clock { get; init; }

        public required Guid Specialist { get; init; }

        public required Guid Controller { get; init; }

        public SimulatedEcfProvider Provider { get; } = new();

        public RecordingUpdater Updater { get; } = new();

        public static async Task<World> CreateAsync(PostgresFixture postgres)
        {
            var clock = new FakeClock();
            var h = await TestHarness.CreateAsync(postgres, clock);
            await h.GrantAsync(h.CompanyId, Daily, "PROCESO_DIARIO");
            return new World
            {
                H = h,
                Clock = clock,
                Specialist = await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"),
                Controller = await h.SessionWithRolesAsync("CONTROLLER"),
            };
        }

        public async Task<Guid> PrepareAsync(string type, long from, long to, Guid session, string key, DateOnly? validUntil = null)
            => Json(await H.RunAsync(new PrepareEcfSeries(H.CompanyId, session, key, type, from, to, validUntil ?? new DateOnly(2099, 12, 31)), new PrepareEcfSeriesHandler()))
                .GetProperty("seriesId").GetGuid();

        public async Task<Guid> ActiveSeriesAsync(string type, long from, long to, DateOnly? validUntil = null)
        {
            var id = await PrepareAsync(type, from, to, Specialist, $"prep-{type}-{from}", validUntil);
            await H.RunAsync(new ApproveEcfSeries(H.CompanyId, Controller, $"appr-{type}-{from}", id, 1), new ApproveEcfSeriesHandler());
            return id;
        }

        public async Task<EcfEnqueued> QueueAsync(string type, string key, string marker = "")
        {
            var result = await H.RunAsync(new QueueTestEcf(H.CompanyId, H.SessionId, key, type, marker), new QueueTestEcfHandler());
            var json = Json(result);
            return new EcfEnqueued(json.GetProperty("documentId").GetGuid(), json.GetProperty("encf").GetString()!, 1);
        }

        /// <summary>A fresh SERVICE session of the daily process (the fake clock moves past idle limits).</summary>
        public Task<Guid> ServiceAsync() => H.Sessions.StartServiceSessionAsync(Daily);

        public async Task<string> AdvanceAsync(Guid document)
        {
            var session = await ServiceAsync();
            var result = await H.RunAsync(new AdvanceEcfDocument(H.CompanyId, session, $"adv-{Interlocked.Increment(ref _keys)}", document), new AdvanceEcfDocumentHandler(Provider, [Updater]));
            return Json(result).GetProperty("outcome").GetString()!.Split(',')[0];
        }

        public async Task<JsonElement> ResolveAsync(Guid document, string resolution, string? providerId, string note, long? version = null)
        {
            var current = version ?? await H.ScalarAsync<long>("SELECT version FROM tax.ecf_document WHERE document_id = @d", ("d", document));
            var result = await H.RunAsync(
                new ResolveEcfDocument(H.CompanyId, Specialist, $"res-{Interlocked.Increment(ref _keys)}", document, current, resolution, providerId, note),
                new ResolveEcfDocumentHandler([Updater]));
            return Json(result);
        }

        public async Task<List<Guid>> DueAsync()
        {
            var json = await H.QueryAsync(new ListDueEcfDocuments(H.CompanyId, await ServiceAsync()), new ListDueEcfDocumentsHandler());
            return [.. JsonDocument.Parse(json).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("documentId").GetGuid())];
        }

        public Task<string?> StatusAsync(Guid document)
            => H.ScalarAsync<string>("SELECT status FROM tax.ecf_document WHERE document_id = @d", ("d", document));
    }

    private sealed class RecordingUpdater : IEcfSourceUpdater
    {
        public List<EcfDocumentSnapshot> Seen { get; } = [];

        public string SourceKind => "INVOICE";

        public Task OnStatusAsync(CommandContext context, EcfDocumentSnapshot document, CancellationToken cancellationToken)
        {
            Seen.Add(document);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Test-only: queues an e-CF of <paramref name="Type"/> for a made-up invoice (or credit note, for 34), with a simulator marker.</summary>
public sealed record QueueTestEcf(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Type, string Marker) : ICommand;

[RequiresPermission("test:ping")]
public sealed class QueueTestEcfHandler : ICommandHandler<QueueTestEcf>
{
    public string CommandType => "Test.QueueEcf";

    public async Task<string> HandleAsync(QueueTestEcf command, CommandContext context, CancellationToken cancellationToken)
    {
        var queued = await EcfQueue.EnqueueAsync(
            context,
            command.Type == "34" ? "CREDIT_NOTE" : "INVOICE",
            context.ResultRef,
            command.Type,
            new DateOnly(2026, 11, 2),
            (encf, due) => new JsonObject
            {
                ["idDoc"] = new JsonObject { ["encf"] = encf, ["sequenceDueDate"] = due.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) },
                ["note"] = command.Marker,
            },
            CommandType,
            cancellationToken);
        return JsonSerializer.Serialize(new { documentId = queued.DocumentId, encf = queued.Encf });
    }
}
