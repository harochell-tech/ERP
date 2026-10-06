using System.Globalization;
using Rochell.MasterData.BankAccounts;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Treasury.Payments;

internal static class PaymentRules
{
    public const string Aggregate = "Payment";

    public sealed record ApDoc(Guid ApDocId, Guid PartyId, DateOnly DocDate, decimal OpenAmount, string Currency = "DOP");

    public sealed record PaymentRow(
        Guid PartyId,
        Guid BankAccountId,
        Guid PartyBankAccountId,
        decimal Amount,
        DateOnly ValueDate,
        string Status,
        Guid PreparedBy,
        long Version,
        string PaymentNo);

    public sealed record Allocation(Guid ApDocId, decimal Amount);

    public static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Checks a payment plan (prepare and update): supplier ACTIVE, bank account ACTIVE, supplier account VERIFIED and of this
    /// supplier (E-VS2-03-8), applications to this supplier's AP documents, each ≤ its open amount now (PAY-03; no reservation,
    /// E-VS2-6), 2 decimals, value date not before the latest invoice (E-VS2-03-4). Returns the payment amount.
    /// </summary>
    public static async Task<decimal> ValidatePlanAsync(
        CommandContext context,
        Guid partyId,
        Guid bankAccountId,
        Guid partyBankAccountId,
        DateOnly valueDate,
        IReadOnlyList<PaymentApplication> applications,
        CancellationToken cancellationToken)
    {
        if (applications is null || applications.Count == 0)
        {
            throw new DomainException(PaymentErrors.ApplicationsRequired, "A payment applies to at least one invoice.");
        }

        if (applications.Select(a => a.ApDocId).Distinct().Count() != applications.Count)
        {
            throw new DomainException(PaymentErrors.ApplicationDuplicate, "An invoice appears more than once in the payment (E-VS2-03-1).");
        }

        foreach (var a in applications)
        {
            if (a.Amount <= 0 || decimal.Round(a.Amount, 2) != a.Amount)
            {
                throw new DomainException(PaymentErrors.AmountInvalid, $"Application amounts are positive with at most 2 decimals (E-VS2-03-5): {a.Amount}.");
            }
        }

        var supplier = await ScalarAsync<string>(
            context,
            "SELECT status::text FROM md.party WHERE party_id = @p AND company_id = @c AND is_supplier",
            cancellationToken,
            ("p", partyId),
            ("c", context.CompanyId)).ConfigureAwait(false);
        if (supplier != "ACTIVE")
        {
            throw new DomainException(PaymentErrors.SupplierNotActive, "Payments are made only to ACTIVE suppliers.");
        }

        var bank = await ScalarAsync<string>(
            context,
            "SELECT status FROM fin.bank_account WHERE bank_account_id = @b AND company_id = @c",
            cancellationToken,
            ("b", bankAccountId),
            ("c", context.CompanyId)).ConfigureAwait(false);
        if (bank != "ACTIVE")
        {
            throw new DomainException(PaymentErrors.BankAccountNotActive, "The bank account does not exist or is closed.");
        }

        var payability = await PartyBankAccounts.PayabilityAsync(
            context.Connection, context.Transaction, context.CompanyId, partyId, partyBankAccountId, context.Clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (payability is not (Payability.Payable or Payability.HoldPending))
        {
            throw new DomainException(PaymentErrors.PartyBankAccountNotVerified, $"The supplier's bank account is not a VERIFIED account of this supplier ({payability}; E-VS2-03-8).");
        }

        var docs = await ReadApDocsAsync(context, applications.Select(a => a.ApDocId).ToArray(), false, cancellationToken).ConfigureAwait(false);
        foreach (var a in applications)
        {
            var doc = docs.GetValueOrDefault(a.ApDocId)
                ?? throw new DomainException(PaymentErrors.NotFound, $"AP document {a.ApDocId} does not exist.");
            if (doc.PartyId != partyId)
            {
                throw new DomainException(PaymentErrors.ApplicationWrongSupplier, $"AP document {a.ApDocId} belongs to another supplier (E-VS2-9).");
            }

            if (doc.Currency != "DOP")
            {
                // USD payables are paid with the exchange difference of USD1-05 (E-USD-7); a peso payment never settles them.
                throw new DomainException(PaymentErrors.ApDocumentCurrency, $"AP document {a.ApDocId} is in {doc.Currency}; it is paid in its currency (E-USD-7).");
            }

            if (a.Amount > doc.OpenAmount)
            {
                throw new DomainException(PaymentErrors.ApplicationExceedsOpenAmount, $"{Money(a.Amount)} exceeds the open amount {Money(doc.OpenAmount)} of AP document {a.ApDocId} (PAY-03).");
            }

            if (valueDate < doc.DocDate)
            {
                throw new DomainException(PaymentErrors.ValueDateBeforeInvoice, $"The value date is before the invoice date {doc.DocDate:yyyy-MM-dd} (E-VS2-03-4).");
            }
        }

        return applications.Sum(a => a.Amount);
    }

    /// <summary>AP documents of this company by id; with <paramref name="lockRows"/> they are locked in id order (VS#2 §5, N8).</summary>
    public static async Task<Dictionary<Guid, ApDoc>> ReadApDocsAsync(CommandContext context, Guid[] ids, bool lockRows, CancellationToken cancellationToken)
    {
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT ap_doc_id, party_id, doc_date, open_amount, currency FROM fin.ap_document
            WHERE company_id = @c AND ap_doc_id = ANY(@ids)
            ORDER BY ap_doc_id
            {(lockRows ? "FOR UPDATE" : string.Empty)}
            """,
            r => new ApDoc(r.GetGuid(0), r.GetGuid(1), r.Date(2), r.GetDecimal(3), r.GetString(4).Trim()),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", ids)).ConfigureAwait(false);
        return rows.ToDictionary(d => d.ApDocId);
    }

    public static async Task<PaymentRow> ReadLockedAsync(CommandContext context, Guid paymentId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT party_id, bank_account_id, party_bank_account_id, amount, value_date, status::text, prepared_by, version, payment_no
            FROM fin.payment WHERE payment_id = @id AND company_id = @c
            FOR UPDATE
            """,
            r => new PaymentRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetDecimal(3), r.Date(4), r.GetString(5), r.GetGuid(6), r.GetInt64(7), r.GetString(8)),
            cancellationToken,
            ("id", paymentId),
            ("c", context.CompanyId)).ConfigureAwait(false)
            ?? throw new DomainException(PaymentErrors.NotFound, "The payment does not exist.");
        if (row.Version != expectedVersion)
        {
            throw new DomainException(PaymentErrors.VersionConflict, $"The payment is at version {row.Version}, not {expectedVersion}.");
        }

        if (row.Status != "PREPARED")
        {
            throw new DomainException(PaymentErrors.NotPrepared, $"The payment is {row.Status}; only a PREPARED payment can change.");
        }

        return row;
    }

    public static async Task WriteAllocationsAsync(CommandContext context, Guid paymentId, long version, IReadOnlyList<PaymentApplication> applications, CancellationToken cancellationToken)
    {
        foreach (var a in applications.OrderBy(a => a.ApDocId))
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO fin.payment_allocation (company_id, payment_id, payment_version, ap_doc_id, amount) VALUES (@c, @p, @v, @d, @a)",
                cancellationToken,
                ("c", context.CompanyId),
                ("p", paymentId),
                ("v", version),
                ("d", a.ApDocId),
                ("a", a.Amount)).ConfigureAwait(false);
        }
    }

    public static object ApplicationsPayload(IEnumerable<PaymentApplication> applications)
        => applications.OrderBy(a => a.ApDocId).Select(a => new { apDocId = a.ApDocId, amount = Money(a.Amount) }).ToList();

    public static string? Reference(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static async Task<T?> ScalarAsync<T>(CommandContext context, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }
}
