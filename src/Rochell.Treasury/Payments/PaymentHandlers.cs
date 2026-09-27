using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.MasterData.BankAccounts;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Treasury.Payments;

[RequiresPermission("payment:prepare")]
public sealed class PrepareSupplierPaymentHandler : ICommandHandler<PrepareSupplierPayment>
{
    public string CommandType => "Treasury.PrepareSupplierPayment";

    public async Task<string> HandleAsync(PrepareSupplierPayment command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var amount = await PaymentRules.ValidatePlanAsync(
            context, command.PartyId, command.BankAccountId, command.PartyBankAccountId, command.ValueDate, command.Applications, cancellationToken).ConfigureAwait(false);
        var preparer = await PaymentRules.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var reference = PaymentRules.Reference(command.BankReference);

        // E-VS2-03-7: the next PAG-number of the company, serialized per company.
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended('payment-no:' || @c, 0))",
            cancellationToken,
            ("c", context.CompanyId.ToString())).ConfigureAwait(false);
        var last = await PaymentRules.ScalarAsync<int?>(
            context,
            "SELECT max(substring(payment_no FROM 5)::int) FROM fin.payment WHERE company_id = @c",
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var paymentNo = $"PAG-{(last ?? 0) + 1:D6}";

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierPaymentPrepared",
                1,
                PaymentRules.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    paymentId = context.ResultRef,
                    paymentNo,
                    partyId = command.PartyId,
                    bankAccountId = command.BankAccountId,
                    partyBankAccountId = command.PartyBankAccountId,
                    amount = PaymentRules.Money(amount),
                    valueDate = command.ValueDate,
                    bankReference = reference,
                    applications = PaymentRules.ApplicationsPayload(command.Applications),
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.payment (payment_id, company_id, direction, party_id, bank_account_id, party_bank_account_id, method, amount, currency,
              value_date, bank_reference, status, prepared_by, version, payment_no)
            VALUES (@id, @c, 'DISBURSEMENT', @party, @bank, @party_bank, 'TRANSFER', @amount, 'DOP', @value_date, @reference, 'PREPARED', @preparer, 1, @no)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("party", command.PartyId),
            ("bank", command.BankAccountId),
            ("party_bank", command.PartyBankAccountId),
            ("amount", amount),
            ("value_date", command.ValueDate),
            ("reference", reference),
            ("preparer", preparer),
            ("no", paymentNo)).ConfigureAwait(false);
        await PaymentRules.WriteAllocationsAsync(context, context.ResultRef, 1, command.Applications, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(PaymentRules.Aggregate, context.ResultRef, "DOCUMENT", null, "PREPARED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { paymentId = context.ResultRef, paymentNo, status = "PREPARED", amount = PaymentRules.Money(amount), version = 1 });
    }
}

[RequiresPermission("payment:prepare")]
public sealed class UpdatePreparedPaymentHandler : ICommandHandler<UpdatePreparedPayment>
{
    public string CommandType => "Treasury.UpdatePreparedPayment";

    public async Task<string> HandleAsync(UpdatePreparedPayment command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await PaymentRules.ReadLockedAsync(context, command.PaymentId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var amount = await PaymentRules.ValidatePlanAsync(
            context, row.PartyId, command.BankAccountId, command.PartyBankAccountId, command.ValueDate, command.Applications, cancellationToken).ConfigureAwait(false);
        var reference = PaymentRules.Reference(command.BankReference);
        var version = row.Version + 1;

        await context.AppendEventAsync(
            new EventDraft(
                "SupplierPaymentUpdated",
                1,
                PaymentRules.Aggregate,
                command.PaymentId,
                version,
                JsonSerializer.Serialize(new
                {
                    paymentId = command.PaymentId,
                    paymentNo = row.PaymentNo,
                    bankAccountId = command.BankAccountId,
                    partyBankAccountId = command.PartyBankAccountId,
                    amount = PaymentRules.Money(amount),
                    valueDate = command.ValueDate,
                    bankReference = reference,
                    applications = PaymentRules.ApplicationsPayload(command.Applications),
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE fin.payment SET bank_account_id = @bank, party_bank_account_id = @party_bank, amount = @amount, value_date = @value_date,
              bank_reference = @reference, version = @version
            WHERE payment_id = @id
            """,
            cancellationToken,
            ("bank", command.BankAccountId),
            ("party_bank", command.PartyBankAccountId),
            ("amount", amount),
            ("value_date", command.ValueDate),
            ("reference", reference),
            ("version", version),
            ("id", command.PaymentId)).ConfigureAwait(false);
        await PaymentRules.WriteAllocationsAsync(context, command.PaymentId, version, command.Applications, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { paymentId = command.PaymentId, status = "PREPARED", amount = PaymentRules.Money(amount), version });
    }
}

[RequiresPermission("payment:void")]
public sealed class VoidPaymentHandler : ICommandHandler<VoidPayment>
{
    public string CommandType => "Treasury.VoidPayment";

    public async Task<string> HandleAsync(VoidPayment command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = string.IsNullOrWhiteSpace(command.Reason)
            ? throw new DomainException(PaymentErrors.ReasonRequired, "Voiding a payment needs a reason (E-VS2-03-6).")
            : command.Reason.Trim();
        var row = await PaymentRules.ReadLockedAsync(context, command.PaymentId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("SupplierPaymentVoided", 1, PaymentRules.Aggregate, command.PaymentId, version, JsonSerializer.Serialize(new { paymentId = command.PaymentId, paymentNo = row.PaymentNo, reason }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.payment SET status = 'VOIDED', version = @version WHERE payment_id = @id",
            cancellationToken,
            ("version", version),
            ("id", command.PaymentId)).ConfigureAwait(false);
        await context.AppendStateAsync(PaymentRules.Aggregate, command.PaymentId, "DOCUMENT", "PREPARED", "VOIDED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { paymentId = command.PaymentId, status = "VOIDED", version });
    }
}

[RequiresPermission("payment:release", StepUp = true)]
public sealed class ReleaseSupplierPaymentHandler : ICommandHandler<ReleaseSupplierPayment>
{
    public const string RuleCode = "R-09";

    private readonly PostingEngine _engine = new();

    public string CommandType => "Treasury.ReleaseSupplierPayment";

    public async Task<string> HandleAsync(ReleaseSupplierPayment command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        // Lock order (VS#2 §5): payment → AP documents by id → bank account → period × components (engine).
        var row = await PaymentRules.ReadLockedAsync(context, command.PaymentId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var releaser = await PaymentRules.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (releaser == row.PreparedBy)
        {
            throw new DomainException(PaymentErrors.SamePerson, "The preparer cannot release the payment (PAY-04).");
        }

        var allocations = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT ap_doc_id, amount FROM fin.payment_allocation WHERE payment_id = @p AND payment_version = @v ORDER BY ap_doc_id",
            r => new PaymentRules.Allocation(r.GetGuid(0), r.GetDecimal(1)),
            cancellationToken,
            ("p", command.PaymentId),
            ("v", row.Version)).ConfigureAwait(false);
        var docs = await PaymentRules.ReadApDocsAsync(context, allocations.Select(a => a.ApDocId).ToArray(), lockRows: true, cancellationToken).ConfigureAwait(false);
        foreach (var a in allocations)
        {
            var open = docs[a.ApDocId].OpenAmount;
            if (a.Amount > open)
            {
                throw new DomainException(
                    PaymentErrors.ApplicationExceedsOpenAmount,
                    $"{PaymentRules.Money(a.Amount)} exceeds the open amount {PaymentRules.Money(open)} of AP document {a.ApDocId} now (PAY-05).");
            }
        }

        var bankStatus = await PaymentRules.ScalarAsync<string>(
            context,
            "SELECT status FROM fin.bank_account WHERE bank_account_id = @b FOR UPDATE",
            cancellationToken,
            ("b", row.BankAccountId)).ConfigureAwait(false);
        if (bankStatus != "ACTIVE")
        {
            throw new DomainException(PaymentErrors.BankAccountNotActive, "The bank account is closed.");
        }

        var now = context.Clock.UtcNow;
        var payability = await PartyBankAccounts.PayabilityAsync(
            context.Connection, context.Transaction, context.CompanyId, row.PartyId, row.PartyBankAccountId, now, cancellationToken).ConfigureAwait(false);
        if (payability != Payability.Payable)
        {
            throw new DomainException(
                PaymentErrors.PartyBankAccountNotPayable,
                payability == Payability.HoldPending
                    ? "The supplier's bank account is still within its 72-hour hold (PAY-06)."
                    : $"The supplier's bank account is not payable ({payability}): it was superseded or never verified (PAY-07).");
        }

        if (row.ValueDate > BusinessCalendar.DefaultBusinessDate(now))
        {
            throw new DomainException(PaymentErrors.ValueDateInFuture, "A payment is released on or after its value date (E-VS2-03-4).");
        }

        var number = new Dictionary<string, string> { ["payment_no"] = row.PaymentNo };
        var lines = allocations
            .Select(a => new PostingLineInput("R09-DR-AP", "applied_amount", a.Amount, PartyId: row.PartyId, SubledgerRef: a.ApDocId, Inputs: number))
            .Append(new PostingLineInput("R09-CR-BANK", "payment_amount", row.Amount, PartyId: row.PartyId, SubledgerRef: row.BankAccountId, Inputs: number))
            .ToList();
        var plan = await _engine.PrepareAsync(context, new PostingRequest(RuleCode, row.ValueDate, now, lines), cancellationToken).ConfigureAwait(false);

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierPaymentReleased",
                1,
                PaymentRules.Aggregate,
                command.PaymentId,
                version,
                JsonSerializer.Serialize(new
                {
                    paymentId = command.PaymentId,
                    paymentNo = row.PaymentNo,
                    partyId = row.PartyId,
                    bankAccountId = row.BankAccountId,
                    amount = PaymentRules.Money(row.Amount),
                    valueDate = row.ValueDate,
                    postingDate = plan.PostingDate,
                    lateEntry = plan.LateEntry,
                    applications = allocations.Select(a => new
                    {
                        apDocId = a.ApDocId,
                        amount = PaymentRules.Money(a.Amount),
                        openBefore = PaymentRules.Money(docs[a.ApDocId].OpenAmount),
                        openAfter = PaymentRules.Money(docs[a.ApDocId].OpenAmount - a.Amount),
                    }),
                }),
                Publish: true,
                BusinessDate: row.ValueDate),
            cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);

        foreach (var a in allocations)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO fin.ap_application (application_id, company_id, payment_id, ap_doc_id, amount, event_id) VALUES (@id, @c, @p, @d, @a, @e)",
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("p", command.PaymentId),
                ("d", a.ApDocId),
                ("a", a.Amount),
                ("e", eventId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE fin.ap_document SET open_amount = open_amount - @a, version = version + 1 WHERE ap_doc_id = @d",
                cancellationToken,
                ("a", a.Amount),
                ("d", a.ApDocId)).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fin.payment SET status = 'RELEASED', released_by = @releaser, posting_event_id = @event, version = @version WHERE payment_id = @id",
            cancellationToken,
            ("releaser", releaser),
            ("event", eventId),
            ("version", version),
            ("id", command.PaymentId)).ConfigureAwait(false);
        await context.AppendStateAsync(PaymentRules.Aggregate, command.PaymentId, "DOCUMENT", "PREPARED", "RELEASED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            paymentId = command.PaymentId,
            status = "RELEASED",
            version,
            journalId = journal.JournalId,
            postingDate = journal.PostingDate,
            lateEntry = journal.LateEntry,
        });
    }
}
