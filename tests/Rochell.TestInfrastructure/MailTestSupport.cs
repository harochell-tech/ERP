using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Mail;

namespace Rochell.TestInfrastructure;

/// <summary>Test-only command that queues one message (MAIL-01). Never exists in production assemblies (ArchitectureTests).</summary>
public sealed record QueueTestMail(Guid CompanyId, Guid SessionId, string IdempotencyKey, IReadOnlyList<string> Recipients, string Subject = "Cotización COT-000001", string Body = "Adjuntamos su cotización.") : ICommand;

[RequiresPermission("test:ping")]
public sealed class QueueTestMailHandler : ICommandHandler<QueueTestMail>
{
    public const string Html = "<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>COT-000001</title></head><body><h1>Cotización COT-000001</h1><p>Bloque de 6 pulgadas — 100 un</p></body></html>";

    public string CommandType => "Test.QueueMail";

    public async Task<string> HandleAsync(QueueTestMail command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var eventId = await context.AppendEventAsync(new EventDraft("TestMailRequested", 1, "TestMail", context.ResultRef, 1, "{}", Publish: false), cancellationToken);
        await using var who = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        var user = (Guid)(await who.ExecuteScalarAsync(cancellationToken))!;
        var mail = await MailOutbox.EnqueueAsync(
            context, new MailDraft("QUOTE", context.ResultRef, "COT-000001", null, command.Recipients, command.Subject, command.Body, "COT-000001.pdf", Html), eventId, user, cancellationToken);
        return JsonSerializer.Serialize(new { mailId = mail });
    }
}

/// <summary>Records what would be sent; fails the first <see cref="FailuresLeft"/> sends.</summary>
public sealed class RecordingMailTransport : IMailTransport
{
    public ConcurrentQueue<MailEnvelope> Sent { get; } = new();

    public int FailuresLeft { get; set; }

    public Task SendAsync(MailEnvelope envelope, CancellationToken cancellationToken)
    {
        if (FailuresLeft > 0)
        {
            FailuresLeft--;
            throw new InvalidOperationException("421 4.7.0 Try again later");
        }

        Sent.Enqueue(envelope);
        return Task.CompletedTask;
    }
}

/// <summary>A renderer that returns a tiny PDF-looking file carrying the HTML's length, and counts its calls.</summary>
public sealed class FakePdfRenderer : IPdfRenderer
{
    private int _calls;

    public int Calls => _calls;

    public bool ReturnGarbage { get; set; }

    public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Encoding.ASCII.GetBytes(ReturnGarbage ? "<html>error</html>" : $"%PDF-1.7\n% test {html.Length}\n%%EOF\n"));
    }
}
