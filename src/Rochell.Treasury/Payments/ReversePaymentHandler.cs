using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Treasury.Payments;

[RequiresPermission("payment:reverse", StepUp = true)]
public sealed class ReversePaymentHandler : ICommandHandler<ReversePayment>
{
    /// <summary>E-VS2-04-5: the reason tells what happened (returned transfer, wrong account…), not just "error".</summary>
    public const int MinimumReasonLength = 10;

    private readonly PostingEngine _engine = new();

    public string CommandType => "Treasury.ReversePayment";

    private sealed record Payment(Guid PartyId, Guid BankAccountId, decimal Amount, string Status, long Version, string PaymentNo, Guid? PostingEventId);

    private sealed record LiveApplication(Guid ApplicationId, Guid ApDocId, decimal Amount);

    public async Task<string> HandleAsync(ReversePayment command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length < MinimumReasonLength)
        {
            throw new DomainException(PaymentErrors.ReasonRequired, "Reversing a payment needs a reason of at least 10 characters (E-VS2-04-5).");
        }

        // Lock order (VS#2 §5): payment → AP documents by id → period × components (engine).
        var payment = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT party_id, bank_account_id, amount, status::text, version, payment_no, posting_event_id
            FROM fin.payment WHERE payment_id = @id AND company_id = @c FOR UPDATE
            """,
            r => new Payment(r.GetGuid(0), r.GetGuid(1), r.GetDecimal(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.NullableGuid(6)),
            cancellationToken,
            ("id", command.PaymentId),
            ("c", context.CompanyId)).ConfigureAwait(false)
            ?? throw new DomainException(PaymentErrors.NotFound, "The payment does not exist.");
        if (payment.Version != command.ExpectedVersion)
        {
            throw new DomainException(PaymentErrors.VersionConflict, $"The payment is at version {payment.Version}, not {command.ExpectedVersion}.");
        }

        if (payment.Status is not ("RELEASED" or "CLEARED"))
        {
            throw new DomainException(PaymentErrors.NotReversible, $"The payment is {payment.Status}; only a RELEASED or CLEARED payment is reversed (a PREPARED one is voided).");
        }

        var applications = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.application_id, a.ap_doc_id, a.amount FROM fin.ap_application a
            WHERE a.payment_id = @p AND a.reverses_application_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM fin.ap_application r WHERE r.reverses_application_id = a.application_id)
            ORDER BY a.ap_doc_id
            """,
            r => new LiveApplication(r.GetGuid(0), r.GetGuid(1), r.GetDecimal(2)),
            cancellationToken,
            ("p", command.PaymentId)).ConfigureAwait(false);
        var docs = await PaymentRules.ReadApDocsAsync(context, applications.Select(a => a.ApDocId).Distinct().ToArray(), lockRows: true, cancellationToken).ConfigureAwait(false);

        // E-VS2-04-4: the one live AUTO journal of the payment's posting event (after any repost).
        var journal = await PaymentRules.ScalarAsync<Guid?>(
            context,
            """
            SELECT j.journal_id FROM fin.gl_journal j
            WHERE j.company_id = @c AND j.source_event_id = @e AND j.journal_type = 'AUTO'
              AND NOT EXISTS (SELECT 1 FROM fin.gl_journal x WHERE x.reverses_journal_id = j.journal_id)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("e", payment.PostingEventId)).ConfigureAwait(false)
            ?? throw new DomainException(PaymentErrors.PostingMissing, "The payment has no live journal to reverse.");

        var now = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(now);
        var plan = await _engine.PrepareReversalAsync(context, journal, businessDate, cancellationToken).ConfigureAwait(false);

        var version = payment.Version + 1;
        var reversals = applications.Select(a => (Original: a, ReversalId: context.Ids.NewId())).ToList();
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "PaymentReversed",
                1,
                PaymentRules.Aggregate,
                command.PaymentId,
                version,
                JsonSerializer.Serialize(new
                {
                    paymentId = command.PaymentId,
                    paymentNo = payment.PaymentNo,
                    reason,
                    fromStatus = payment.Status,
                    businessDate,
                    postingDate = plan.PostingDate,
                    lateEntry = plan.LateEntry,
                    reversedJournalId = journal,
                    applications = reversals.Select(r => new
                    {
                        applicationId = r.Original.ApplicationId,
                        reversalId = r.ReversalId,
                        apDocId = r.Original.ApDocId,
                        amount = PaymentRules.Money(r.Original.Amount),
                        openBefore = PaymentRules.Money(docs[r.Original.ApDocId].OpenAmount),
                        openAfter = PaymentRules.Money(docs[r.Original.ApDocId].OpenAmount + r.Original.Amount),
                    }),
                }),
                Publish: true,
                BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, now, cancellationToken).ConfigureAwait(false);

        foreach (var (original, reversalId) in reversals)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.ap_application (application_id, company_id, payment_id, ap_doc_id, amount, event_id, reverses_application_id)
                VALUES (@id, @c, @p, @d, @a, @e, @original)
                """,
                cancellationToken,
                ("id", reversalId),
                ("c", context.CompanyId),
                ("p", command.PaymentId),
                ("d", original.ApDocId),
                ("a", original.Amount),
                ("e", eventId),
                ("original", original.ApplicationId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE fin.ap_document SET open_amount = open_amount + @a, version = version + 1 WHERE ap_doc_id = @d",
                cancellationToken,
                ("a", original.Amount),
                ("d", original.ApDocId)).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.payment SET status = 'REVERSED', version = @version WHERE payment_id = @id",
            cancellationToken,
            ("version", version),
            ("id", command.PaymentId)).ConfigureAwait(false);
        await context.AppendStateAsync(PaymentRules.Aggregate, command.PaymentId, "DOCUMENT", payment.Status, "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            paymentId = command.PaymentId,
            status = "REVERSED",
            version,
            journalId = reversal.JournalId,
            postingDate = reversal.PostingDate,
            lateEntry = reversal.LateEntry,
        });
    }
}
