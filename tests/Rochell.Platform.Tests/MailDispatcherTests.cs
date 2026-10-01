using System.Security.Cryptography;
using Rochell.Platform.Commands;
using Rochell.Platform.Mail;
using Rochell.Platform.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Platform.Tests;

/// <summary>MAIL-01 (E-MAIL-7, E-MAIL-01-4, 5, 6, 10): the mail queue and its dispatcher, with a recording transport and a fake renderer.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MailDispatcherTests(PostgresFixture postgres)
{
    private const string Internal = "industrias@rochell.com.do";

    private static readonly MailDelivery Live = new(MailMode.Live, null, Internal);
    private static readonly MailDelivery Redirect = new(MailMode.Redirect, Internal, Internal);

    private static Task<CommandResult> QueueAsync(TestHarness h, string key, params string[] recipients)
        => h.RunAsync(new QueueTestMail(h.CompanyId, h.SessionId, key, recipients), new QueueTestMailHandler());

    private static Task<string?> StateAsync(TestHarness h)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(status || ':' || attempts || ':' || coalesce(delivery_mode, '-') || ':' || coalesce(array_to_string(delivered_to, '+'), '-') || ':' || (pdf IS NOT NULL)::text,
                              ',' ORDER BY requested_at, mail_id)
            FROM core.mail_message
            """);

    [Fact]
    public async Task A_queued_message_is_rendered_once_sent_to_its_recipients_with_the_archive_copy_and_kept_with_its_PDF()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var transport = new RecordingMailTransport();
        var renderer = new FakePdfRenderer();
        await QueueAsync(h, "m1", " Compras@Cliente.com.do ", "obra@cliente.com.do", "compras@cliente.com.do");

        var sent = await new MailDispatcher(h.App, transport, renderer, Live, clock).DispatchPendingAsync();
        var again = await new MailDispatcher(h.App, transport, renderer, Live, clock).DispatchPendingAsync();

        Assert.Equal((1, 0, 1), (sent, again, renderer.Calls));
        var envelope = Assert.Single(transport.Sent);
        Assert.Equal(["compras@cliente.com.do", "obra@cliente.com.do"], envelope.To);
        Assert.Equal((Internal, "Cotización COT-000001", "Adjuntamos su cotización.", "COT-000001.pdf"), (envelope.Bcc, envelope.Subject, envelope.BodyText, envelope.FileName));
        Assert.Equal("SENT:1:LIVE:compras@cliente.com.do+obra@cliente.com.do:true", await StateAsync(h));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(envelope.Pdf)), await h.ScalarAsync<string>("SELECT pdf_sha256 FROM core.mail_message"));
        Assert.Equal("1:SENT:", await h.ScalarAsync<string>("SELECT string_agg(attempt_no || ':' || outcome || ':' || coalesce(error, ''), ',' ORDER BY attempt_no) FROM core.mail_attempt"));
    }

    [Fact]
    public async Task In_redirect_mode_no_customer_address_is_on_the_envelope()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var transport = new RecordingMailTransport();
        await QueueAsync(h, "m1", "compras@cliente.com.do", "obra@cliente.com.do");

        await new MailDispatcher(h.App, transport, new FakePdfRenderer(), Redirect, clock).DispatchPendingAsync();

        var envelope = Assert.Single(transport.Sent);
        Assert.Equal([Internal], envelope.To);
        Assert.Null(envelope.Bcc);
        Assert.Equal("[Redirigido — para: compras@cliente.com.do +1] Cotización COT-000001", envelope.Subject);
        Assert.StartsWith("Correo redirigido: no se envió al cliente. Destinatarios originales: compras@cliente.com.do, obra@cliente.com.do\n\nAdjuntamos", envelope.BodyText, StringComparison.Ordinal);
        Assert.Equal($"SENT:1:REDIRECT:{Internal}:true", await StateAsync(h));
    }

    [Fact]
    public async Task A_failed_attempt_waits_longer_each_time_keeps_the_PDF_and_after_five_the_message_is_FAILED_until_retried()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var transport = new RecordingMailTransport { FailuresLeft = 5 };
        var renderer = new FakePdfRenderer();
        var dispatcher = new MailDispatcher(h.App, transport, renderer, Live, clock);
        await QueueAsync(h, "m1", "compras@cliente.com.do");

        // Attempt 1 fails: the next one is due a minute later, not before.
        Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        Assert.Equal("QUEUED:1:-:-:true", await StateAsync(h));
        Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        Assert.Equal(1L, await h.CountAsync("core.mail_attempt"));

        // 1, 5, 15 and 60 minutes after each failure; the fifth failure ends it.
        foreach (var minutes in new[] { 1, 5, 15, 60 })
        {
            clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await dispatcher.DispatchPendingAsync());
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        }

        Assert.Equal("FAILED:5:-:-:true", await StateAsync(h));
        Assert.Equal("421 4.7.0 Try again later", await h.ScalarAsync<string>("SELECT last_error FROM core.mail_message"));
        Assert.Equal((5L, 1), (await h.CountAsync("core.mail_attempt"), renderer.Calls));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        Assert.Empty(transport.Sent);

        // The retry a person asks for: FAILED → QUEUED, due now.
        Assert.Null(await h.AppExecuteAsync($"UPDATE core.mail_message SET status = 'QUEUED', attempts = 0, next_attempt_at = '{clock.UtcNow:O}'"));
        Assert.Equal(1, await dispatcher.DispatchPendingAsync());
        Assert.Equal("SENT:1:LIVE:compras@cliente.com.do:true", await StateAsync(h));
        Assert.Equal((6L, 1), (await h.CountAsync("core.mail_attempt"), renderer.Calls));
    }

    [Fact]
    public async Task A_renderer_that_does_not_return_a_PDF_is_a_failed_attempt_and_Off_dispatches_nothing()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var transport = new RecordingMailTransport();
        await QueueAsync(h, "m1", "compras@cliente.com.do");

        Assert.Equal(0, await new MailDispatcher(h.App, transport, new FakePdfRenderer(), new MailDelivery(MailMode.Off, null, null), clock).DispatchPendingAsync());
        Assert.Equal("QUEUED:0:-:-:false", await StateAsync(h));
        Assert.Equal(0, await new MailDispatcher(h.App, transport, new FakePdfRenderer { ReturnGarbage = true }, Live, clock).DispatchPendingAsync());

        Assert.Equal("QUEUED:1:-:-:false", await StateAsync(h));
        Assert.Equal("The renderer did not return a PDF.", await h.ScalarAsync<string>("SELECT last_error FROM core.mail_message"));
        Assert.Empty(transport.Sent);
        Assert.Throws<ArgumentException>(() => new MailDispatcher(h.App, transport, new FakePdfRenderer(), new MailDelivery(MailMode.Redirect, null, null), clock));
    }

    [Fact]
    public async Task Two_dispatchers_at_once_send_each_message_once()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var transport = new RecordingMailTransport();
        for (var i = 0; i < 6; i++)
        {
            await QueueAsync(h, $"m{i}", $"cliente{i}@cliente.com.do");
        }

        var sent = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => new MailDispatcher(h.App, transport, new FakePdfRenderer(), Live, clock).DispatchPendingAsync()));

        Assert.Equal(6, sent.Sum());
        Assert.Equal(6, transport.Sent.Select(e => e.To[0]).Distinct().Count());
        Assert.Equal(6L, await h.CountAsync("core.mail_attempt"));
    }

    [Fact]
    public async Task Recipients_are_validated_and_what_was_queued_or_sent_cannot_be_changed()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);

        var none = await Assert.ThrowsAsync<DomainException>(() => QueueAsync(h, "none"));
        var malformed = await Assert.ThrowsAsync<DomainException>(() => QueueAsync(h, "bad", "compras@cliente"));
        var many = await Assert.ThrowsAsync<DomainException>(() => QueueAsync(h, "many", [.. Enumerable.Range(0, 11).Select(i => $"c{i}@cliente.com.do")]));
        var subject = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new QueueTestMail(h.CompanyId, h.SessionId, "subject", ["a@cliente.com.do"], "Asunto\r\nBcc: x@y.com"), new QueueTestMailHandler()));
        Assert.Equal(
            (MailErrors.RecipientInvalid, MailErrors.RecipientInvalid, MailErrors.RecipientInvalid, MailErrors.FieldInvalid), (none.Code, malformed.Code, many.Code, subject.Code));
        Assert.Equal(0L, await h.CountAsync("core.mail_message"));

        await QueueAsync(h, "m1", "compras@cliente.com.do");
        var rewrite = await h.AppExecuteAsync("UPDATE core.mail_message SET recipients = ARRAY['otro@cliente.com.do']");
        var html = await h.AdminExecuteAsync("UPDATE core.mail_message SET html = '<p>otro</p>'");
        var skip = await h.AdminExecuteAsync("UPDATE core.mail_message SET status = 'SENT'");
        var delete = await h.AdminExecuteAsync("DELETE FROM core.mail_message");
        await new MailDispatcher(h.App, new RecordingMailTransport(), new FakePdfRenderer(), Live, clock).DispatchPendingAsync();
        var afterSent = await h.AdminExecuteAsync("UPDATE core.mail_message SET status = 'QUEUED'");
        var attempt = await h.AdminExecuteAsync("UPDATE core.mail_attempt SET outcome = 'FAILED', error = 'x'");

        Assert.Equal("42501", rewrite?.SqlState); // no column privilege for the application role
        Assert.Contains("immutable", html?.MessageText, StringComparison.Ordinal);
        Assert.NotNull(skip); // SENT needs sent_at, the mode, the recipients and the PDF
        Assert.Contains("cannot be deleted", delete?.MessageText, StringComparison.Ordinal);
        Assert.Contains("a sent message does not change", afterSent?.MessageText, StringComparison.Ordinal);
        Assert.NotNull(attempt);
    }
}
