using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;
using Rochell.Treasury.Payments;

namespace Rochell.Treasury.Queries;

// E-VS2-07-1: treasury read side. payment:read for AP aging, the payment proposal and payments; bank:read for bank accounts,
// supplier bank accounts, statements and lines. Account numbers leave masked unless the reader holds bank_account_number:read
// (E-VS2-07-3). Lists page like VS#1 (E-VS2-07-7).

public static class TreasuryQueryErrors
{
    public const string PolicyMissing = "POLICY_MISSING";
}

// ---------------------------------------------------------------------------------------------------------------------------
// AP aging (E-VS2-07-2)
// ---------------------------------------------------------------------------------------------------------------------------

public sealed record GetApAging(Guid CompanyId, Guid SessionId, DateOnly? AsOf = null) : IQuery;

public sealed record AgingBuckets(int Bucket1Days, int Bucket2Days, int Bucket3Days);

public sealed record AgingDocument(Guid ApDocId, Guid SupplierInvoiceId, string SupplierFiscalNumber, DateOnly DocDate, DateOnly DueDate, decimal OpenAmount, int DaysOverdue, string Bucket);

public sealed record AgingSupplier(
    Guid SupplierId, string SupplierName, decimal Current, decimal Bucket1, decimal Bucket2, decimal Bucket3, decimal Over, decimal Total, IReadOnlyList<AgingDocument> Documents);

/// <summary>E-UX4-2: the open amount of every supplier per bucket; <c>Total</c> is their sum (the grand total).</summary>
public sealed record AgingBucketTotals(decimal Current, decimal Bucket1, decimal Bucket2, decimal Bucket3, decimal Over, decimal Total);

public sealed record ApAging(DateOnly AsOf, AgingBuckets Buckets, IReadOnlyList<AgingSupplier> Suppliers, decimal Total, AgingBucketTotals BucketTotals);

[RequiresPermission("payment:read")]
public sealed class GetApAgingHandler : IQueryHandler<GetApAging>
{
    public const string Current = "CURRENT";
    public const string Bucket1 = "BUCKET_1";
    public const string Bucket2 = "BUCKET_2";
    public const string Bucket3 = "BUCKET_3";
    public const string Over = "OVER";

    public string QueryType => "Treasury.GetApAging";

    private sealed record Row(Guid ApDocId, Guid InvoiceId, string Ncf, Guid SupplierId, string SupplierName, DateOnly DocDate, DateOnly DueDate, decimal Open);

    public async Task<string> HandleAsync(GetApAging query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var asOf = query.AsOf ?? BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var buckets = await BucketsAsync(context, asOf, cancellationToken).ConfigureAwait(false);
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.ap_doc_id, i.si_id, i.supplier_fiscal_number, d.party_id, p.legal_name, d.doc_date, d.due_date, d.open_amount
            FROM fin.ap_document d
            JOIN pur.supplier_invoice i ON i.si_id = d.source_doc_id AND i.accounting_status::text = 'POSTED'
            JOIN md.party p ON p.party_id = d.party_id
            WHERE d.company_id = @c AND d.open_amount > 0
            ORDER BY p.legal_name, d.party_id, d.due_date, d.ap_doc_id
            """,
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.Date(5), r.Date(6), r.GetDecimal(7)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);

        var suppliers = rows.GroupBy(r => (r.SupplierId, r.SupplierName)).Select(g =>
        {
            var documents = g.Select(r =>
            {
                var days = asOf.DayNumber - r.DueDate.DayNumber;
                return new AgingDocument(r.ApDocId, r.InvoiceId, r.Ncf, r.DocDate, r.DueDate, r.Open, Math.Max(days, 0), BucketOf(days, buckets));
            }).ToList();
            decimal Sum(string bucket) => documents.Where(d => d.Bucket == bucket).Sum(d => d.OpenAmount);
            return new AgingSupplier(
                g.Key.SupplierId, g.Key.SupplierName, Sum(Current), Sum(Bucket1), Sum(Bucket2), Sum(Bucket3), Sum(Over), documents.Sum(d => d.OpenAmount), documents);
        }).ToList();
        var zero = new decimal(0, 0, 0, false, 2);
        var total = zero + suppliers.Sum(s => s.Total);
        var totals = new AgingBucketTotals(
            zero + suppliers.Sum(s => s.Current), zero + suppliers.Sum(s => s.Bucket1), zero + suppliers.Sum(s => s.Bucket2), zero + suppliers.Sum(s => s.Bucket3),
            zero + suppliers.Sum(s => s.Over), total);
        return ApiJson.Serialize(new ApAging(asOf, buckets, suppliers, total, totals));
    }

    public static string BucketOf(int daysOverdue, AgingBuckets buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        return daysOverdue <= 0 ? Current
            : daysOverdue <= buckets.Bucket1Days ? Bucket1
            : daysOverdue <= buckets.Bucket2Days ? Bucket2
            : daysOverdue <= buckets.Bucket3Days ? Bucket3
            : Over;
    }

    /// <summary>The TREASURY policy version in force at the date; none, or buckets not increasing, is refused loudly.</summary>
    private static async Task<AgingBuckets> BucketsAsync(QueryContext context, DateOnly asOf, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT p.param_code, p.value #>> '{}' FROM acc.accounting_policy_version v
            JOIN acc.accounting_policy_parameter p ON p.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = 'TREASURY' AND v.status = 'ACTIVE'
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            """,
            ("c", context.CompanyId),
            ("d", asOf)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values[reader.GetString(0)] = int.Parse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }

        if (!values.TryGetValue("ap_aging_bucket_1_days", out var b1) || !values.TryGetValue("ap_aging_bucket_2_days", out var b2)
            || !values.TryGetValue("ap_aging_bucket_3_days", out var b3))
        {
            throw new DomainException(TreasuryQueryErrors.PolicyMissing, $"No ACTIVE TREASURY policy with the AP aging buckets on {asOf:yyyy-MM-dd} (E-VS2-07-2).");
        }

        return b1 < b2 && b2 < b3
            ? new AgingBuckets(b1, b2, b3)
            : throw new DomainException(TreasuryQueryErrors.PolicyMissing, $"The TREASURY policy's AP aging buckets must increase ({b1}, {b2}, {b3}).");
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Payment proposal (E-VS2-07-4)
// ---------------------------------------------------------------------------------------------------------------------------

public sealed record GetPaymentProposal(Guid CompanyId, Guid SessionId, DateOnly DueUntil, Guid? SupplierId = null) : IQuery;

public sealed record ProposalInvoice(Guid ApDocId, Guid SupplierInvoiceId, string SupplierFiscalNumber, DateOnly DocDate, DateOnly DueDate, decimal OriginalAmount, decimal OpenAmount);

/// <summary><see cref="Payability"/>: PAYABLE, HOLD_PENDING (until <see cref="PayableFrom"/>), REVIEW (a version waits for verification) or NONE.</summary>
public sealed record ProposalSupplier(
    Guid SupplierId, string SupplierName, string SupplierStatus, string Payability, Guid? PartyBankAccountId, string? BankCode, string? AccountNumber, DateTime? PayableFrom,
    decimal OpenAmount, IReadOnlyList<ProposalInvoice> Invoices);

public sealed record PaymentProposal(DateOnly DueUntil, IReadOnlyList<ProposalSupplier> Suppliers);

[RequiresPermission("payment:read")]
public sealed class GetPaymentProposalHandler : IQueryHandler<GetPaymentProposal>
{
    public string QueryType => "Treasury.GetPaymentProposal";

    private sealed record Row(Guid ApDocId, Guid InvoiceId, string Ncf, Guid SupplierId, string SupplierName, string SupplierStatus, DateOnly DocDate, DateOnly DueDate, decimal Original, decimal Open);

    private sealed record Account(Guid PartyId, Guid? AccountId, string? BankCode, string? Number, DateTime? PayableFrom, bool Review);

    public async Task<string> HandleAsync(GetPaymentProposal query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.ap_doc_id, i.si_id, i.supplier_fiscal_number, d.party_id, p.legal_name, p.status::text, d.doc_date, d.due_date, d.original_amount, d.open_amount
            FROM fin.ap_document d
            JOIN pur.supplier_invoice i ON i.si_id = d.source_doc_id AND i.accounting_status::text = 'POSTED'
            JOIN md.party p ON p.party_id = d.party_id
            WHERE d.company_id = @c AND d.open_amount > 0 AND d.due_date <= @until AND (CAST(@supplier AS uuid) IS NULL OR d.party_id = CAST(@supplier AS uuid))
            ORDER BY p.legal_name, d.party_id, d.due_date, d.ap_doc_id
            """,
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.Date(6), r.Date(7), r.GetDecimal(8), r.GetDecimal(9)),
            cancellationToken,
            ("c", context.CompanyId),
            ("until", query.DueUntil),
            ("supplier", query.SupplierId)).ConfigureAwait(false);
        var parties = rows.Select(r => r.SupplierId).Distinct().ToArray();
        var accounts = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.party_id, v.party_bank_account_id, v.bank_code, v.account_number, v.payable_from,
                   EXISTS (SELECT 1 FROM md.party_bank_account r WHERE r.party_id = p.party_id AND r.status = 'REVIEW')
            FROM unnest(@parties) AS p (party_id)
            LEFT JOIN md.party_bank_account v ON v.party_id = p.party_id AND v.status = 'VERIFIED'
            """,
            r => new Account(r.GetGuid(0), r.NullableGuid(1), r.NullableString(2), r.NullableString(3), r.NullableUtc(4), r.GetBoolean(5)),
            cancellationToken,
            ("parties", parties)).ConfigureAwait(false)).ToDictionary(a => a.PartyId);
        var full = await QueryPermissions.HasAsync(context, AccountNumbers.FullPermission, cancellationToken).ConfigureAwait(false);
        var now = context.Clock.UtcNow;

        var suppliers = rows.GroupBy(r => (r.SupplierId, r.SupplierName, r.SupplierStatus)).Select(g =>
        {
            var a = accounts[g.Key.SupplierId];
            var payability = a.AccountId is null ? (a.Review ? "REVIEW" : "NONE") : now >= a.PayableFrom ? "PAYABLE" : "HOLD_PENDING";
            return new ProposalSupplier(
                g.Key.SupplierId, g.Key.SupplierName, g.Key.SupplierStatus, payability, a.AccountId, a.BankCode, a.Number is null ? null : AccountNumbers.Show(a.Number, full), a.PayableFrom,
                g.Sum(r => r.Open),
                g.Select(r => new ProposalInvoice(r.ApDocId, r.InvoiceId, r.Ncf, r.DocDate, r.DueDate, r.Original, r.Open)).ToList());
        }).ToList();
        return ApiJson.Serialize(new PaymentProposal(query.DueUntil, suppliers));
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Payments
// ---------------------------------------------------------------------------------------------------------------------------

public sealed record ListPayments(Guid CompanyId, Guid SessionId, string? Status = null, Guid? SupplierId = null, int Limit = 50, int Offset = 0) : IQuery;

/// <summary>E-UX4-6: <see cref="BankAccountAlias"/> is the bank account's alias, if it has one.</summary>
public sealed record PaymentSummary(
    Guid PaymentId, string PaymentNo, Guid SupplierId, string SupplierName, Guid BankAccountId, string BankCode, string AccountNumber, decimal Amount, DateOnly ValueDate, string Status, long Version,
    string? BankAccountAlias);

/// <summary>E-UX4-2: <see cref="Count"/> and <see cref="Total"/> cover every payment the filter selects (all pages), not only this page.</summary>
public sealed record PaymentList(IReadOnlyList<PaymentSummary> Items, int Limit, int Offset, int Count, decimal Total);

[RequiresPermission("payment:read")]
public sealed class ListPaymentsHandler : IQueryHandler<ListPayments>
{
    public string QueryType => "Treasury.ListPayments";

    public async Task<string> HandleAsync(ListPayments query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var full = await QueryPermissions.HasAsync(context, AccountNumbers.FullPermission, cancellationToken).ConfigureAwait(false);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.payment_id, p.payment_no, p.party_id, s.legal_name, p.bank_account_id, b.bank_code, b.account_number, p.amount, p.value_date, p.status::text, p.version, b.alias
            FROM fin.payment p
            JOIN md.party s ON s.party_id = p.party_id
            JOIN fin.bank_account b ON b.bank_account_id = p.bank_account_id
            WHERE p.company_id = @c AND (CAST(@status AS text) IS NULL OR p.status::text = CAST(@status AS text))
              AND (CAST(@supplier AS uuid) IS NULL OR p.party_id = CAST(@supplier AS uuid))
            ORDER BY p.payment_no DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new PaymentSummary(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), AccountNumbers.Show(r.GetString(6), full), r.GetDecimal(7), r.Date(8), r.GetString(9), r.GetInt64(10),
                r.NullableString(11)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("supplier", query.SupplierId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        var totals = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT count(*)::int, coalesce(sum(p.amount), 0)::numeric(19,2)
            FROM fin.payment p
            WHERE p.company_id = @c AND (CAST(@status AS text) IS NULL OR p.status::text = CAST(@status AS text))
              AND (CAST(@supplier AS uuid) IS NULL OR p.party_id = CAST(@supplier AS uuid))
            """,
            r => (Count: r.GetInt32(0), Total: r.GetDecimal(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("supplier", query.SupplierId)).ConfigureAwait(false)).Single();
        return ApiJson.Serialize(new PaymentList(items, query.Limit, query.Offset, totals.Count, totals.Total));
    }
}

public sealed record GetPayment(Guid CompanyId, Guid SessionId, Guid PaymentId) : IQuery;

/// <summary>An application of a released payment, or its reversal row (<see cref="Reversal"/>); for a PREPARED payment, its plan.</summary>
public sealed record PaymentApplicationView(Guid ApDocId, Guid SupplierInvoiceId, string SupplierFiscalNumber, DateOnly DueDate, decimal Amount, bool Reversal);

public sealed record PaymentLineView(Guid LineId, Guid StatementId, string Direction, DateOnly ValueDate, decimal Amount, string? BankReference, string Description);

public sealed record PaymentDetail(
    Guid PaymentId,
    string PaymentNo,
    Guid SupplierId,
    string SupplierName,
    Guid BankAccountId,
    string BankCode,
    string AccountNumber,
    Guid PartyBankAccountId,
    string PartyBankCode,
    string PartyAccountNumber,
    string PartyAccountStatus,
    decimal Amount,
    DateOnly ValueDate,
    string? BankReference,
    string Status,
    string? PreparedBy,
    string? ReleasedBy,
    Guid? PostingEventId,
    long Version,
    IReadOnlyList<PaymentApplicationView> Applications,
    IReadOnlyList<PaymentApplicationView> Plan,
    IReadOnlyList<PaymentLineView> StatementLines,
    IReadOnlyList<StateChange> History,
    string? BankAccountAlias);

[RequiresPermission("payment:read")]
public sealed class GetPaymentHandler : IQueryHandler<GetPayment>
{
    public string QueryType => "Treasury.GetPayment";

    private sealed record Header(
        string PaymentNo, Guid SupplierId, string SupplierName, Guid BankAccountId, string BankCode, string AccountNumber, Guid PartyAccountId, string PartyBankCode,
        string PartyNumber, string PartyStatus, decimal Amount, DateOnly ValueDate, string? Reference, string Status, string? PreparedBy, string? ReleasedBy, Guid? PostingEventId, long Version,
        string? Alias);

    public async Task<string> HandleAsync(GetPayment query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var h = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.payment_no, p.party_id, s.legal_name, p.bank_account_id, b.bank_code, b.account_number, v.party_bank_account_id, v.bank_code, v.account_number, v.status,
                   p.amount, p.value_date, p.bank_reference, p.status::text, coalesce(pu.display_name, pu.email), coalesce(ru.display_name, ru.email), p.posting_event_id, p.version,
                   b.alias
            FROM fin.payment p
            JOIN md.party s ON s.party_id = p.party_id
            JOIN fin.bank_account b ON b.bank_account_id = p.bank_account_id
            JOIN md.party_bank_account v ON v.party_bank_account_id = p.party_bank_account_id
            LEFT JOIN iam.user pu ON pu.user_id = p.prepared_by
            LEFT JOIN iam.user ru ON ru.user_id = p.released_by
            WHERE p.company_id = @c AND p.payment_id = @id
            """,
            r => new Header(
                r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetGuid(6), r.GetString(7), r.GetString(8), r.GetString(9),
                r.GetDecimal(10), r.Date(11), r.NullableString(12), r.GetString(13), r.NullableString(14), r.NullableString(15), r.NullableGuid(16), r.GetInt64(17), r.NullableString(18)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.PaymentId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The payment does not exist.");
        var full = await QueryPermissions.HasAsync(context, AccountNumbers.FullPermission, cancellationToken).ConfigureAwait(false);

        static PaymentApplicationView Application(System.Data.Common.DbDataReader r)
            => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Date(3), r.GetDecimal(4), r.GetBoolean(5));
        var applications = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.ap_doc_id, i.si_id, i.supplier_fiscal_number, d.due_date, a.amount, a.reverses_application_id IS NOT NULL
            FROM fin.ap_application a
            JOIN fin.ap_document d ON d.ap_doc_id = a.ap_doc_id
            JOIN pur.supplier_invoice i ON i.si_id = d.source_doc_id
            WHERE a.payment_id = @id
            ORDER BY a.reverses_application_id IS NOT NULL, i.supplier_fiscal_number
            """,
            Application,
            cancellationToken,
            ("id", query.PaymentId)).ConfigureAwait(false);
        var plan = h.Status == "PREPARED"
            ? await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT a.ap_doc_id, i.si_id, i.supplier_fiscal_number, d.due_date, a.amount, false
                FROM fin.payment_allocation a
                JOIN fin.ap_document d ON d.ap_doc_id = a.ap_doc_id
                JOIN pur.supplier_invoice i ON i.si_id = d.source_doc_id
                WHERE a.payment_id = @id AND a.payment_version = @v
                ORDER BY i.supplier_fiscal_number
                """,
                Application,
                cancellationToken,
                ("id", query.PaymentId),
                ("v", h.Version)).ConfigureAwait(false)
            : [];
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT line_id, statement_id, direction, value_date, amount, bank_reference, description FROM fin.bank_statement_line
            WHERE matched_payment_id = @id ORDER BY direction DESC
            """,
            r => new PaymentLineView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Date(3), r.GetDecimal(4), r.NullableString(5), r.GetString(6)),
            cancellationToken,
            ("id", query.PaymentId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, PaymentRules.Aggregate, query.PaymentId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new PaymentDetail(
            query.PaymentId, h.PaymentNo, h.SupplierId, h.SupplierName, h.BankAccountId, h.BankCode, AccountNumbers.Show(h.AccountNumber, full), h.PartyAccountId, h.PartyBankCode,
            AccountNumbers.Show(h.PartyNumber, full), h.PartyStatus, h.Amount, h.ValueDate, h.Reference, h.Status, h.PreparedBy, h.ReleasedBy, h.PostingEventId, h.Version,
            applications, plan, lines, history, h.Alias));
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Bank accounts
// ---------------------------------------------------------------------------------------------------------------------------

public sealed record ListBankAccounts(Guid CompanyId, Guid SessionId) : IQuery;

/// <summary>E-UX4-6: <see cref="Alias"/> is the account's short name (SetBankAccountAlias), null when none was given.</summary>
public sealed record BankAccountView(Guid BankAccountId, string BankCode, string AccountNumber, string Currency, string GlAccountCode, string GlAccountName, string Status, long Version, string? Alias);

public sealed record BankAccountList(IReadOnlyList<BankAccountView> Items);

[RequiresPermission("bank:read")]
public sealed class ListBankAccountsHandler : IQueryHandler<ListBankAccounts>
{
    public string QueryType => "Treasury.ListBankAccounts";

    public async Task<string> HandleAsync(ListBankAccounts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var full = await QueryPermissions.HasAsync(context, AccountNumbers.FullPermission, cancellationToken).ConfigureAwait(false);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT b.bank_account_id, b.bank_code, b.account_number, b.currency, a.code, a.name, b.status, b.version, b.alias
            FROM fin.bank_account b JOIN fin.account a ON a.account_id = b.gl_account_id
            WHERE b.company_id = @c ORDER BY b.status, b.bank_code, b.account_number
            """,
            r => new BankAccountView(r.GetGuid(0), r.GetString(1), AccountNumbers.Show(r.GetString(2), full), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), r.NullableString(8)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new BankAccountList(items));
    }
}

public sealed record ListPartyBankAccounts(Guid CompanyId, Guid SessionId, Guid PartyId) : IQuery;

public sealed record PartyBankAccountView(
    Guid PartyBankAccountId, int Version, string BankCode, string AccountNumber, string AccountHolder, string Status, string? RequestedBy, DateTime RequestedAt,
    string? VerifiedBy, DateTime? VerifiedAt, string? VerificationEvidence, DateTime? PayableFrom, string? RejectedBy, DateTime? RejectedAt, string? RejectionReason);

public sealed record PartyBankAccountList(Guid PartyId, IReadOnlyList<PartyBankAccountView> Items);

[RequiresPermission("bank:read")]
public sealed class ListPartyBankAccountsHandler : IQueryHandler<ListPartyBankAccounts>
{
    public string QueryType => "Treasury.ListPartyBankAccounts";

    public async Task<string> HandleAsync(ListPartyBankAccounts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var full = await QueryPermissions.HasAsync(context, AccountNumbers.FullPermission, cancellationToken).ConfigureAwait(false);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.party_bank_account_id, v.version, v.bank_code, v.account_number, v.account_holder, v.status, coalesce(rq.display_name, rq.email), v.requested_at,
                   coalesce(vf.display_name, vf.email), v.verified_at, v.verification_evidence, v.payable_from, coalesce(rj.display_name, rj.email), v.rejected_at, v.rejection_reason
            FROM md.party_bank_account v
            LEFT JOIN iam.user rq ON rq.user_id = v.requested_by
            LEFT JOIN iam.user vf ON vf.user_id = v.verified_by
            LEFT JOIN iam.user rj ON rj.user_id = v.rejected_by
            WHERE v.company_id = @c AND v.party_id = @p ORDER BY v.version DESC
            """,
            r => new PartyBankAccountView(
                r.GetGuid(0), r.GetInt32(1), r.GetString(2), AccountNumbers.Show(r.GetString(3), full), r.GetString(4), r.GetString(5), r.NullableString(6), r.Utc(7),
                r.NullableString(8), r.NullableUtc(9), r.NullableString(10), r.NullableUtc(11), r.NullableString(12), r.NullableUtc(13), r.NullableString(14)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new PartyBankAccountList(query.PartyId, items));
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// Statements and lines
// ---------------------------------------------------------------------------------------------------------------------------

public sealed record ListBankStatements(Guid CompanyId, Guid SessionId, Guid? BankAccountId = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record BankStatementView(
    Guid StatementId, Guid BankAccountId, string BankCode, string AccountNumber, DateOnly PeriodFrom, DateOnly PeriodTo, decimal OpeningBalance, decimal ClosingBalance,
    string FileName, string? ImportedBy, DateTime ImportedAt, int Lines, int Unmatched, string? BankAccountAlias);

public sealed record BankStatementList(IReadOnlyList<BankStatementView> Items, int Limit, int Offset);

[RequiresPermission("bank:read")]
public sealed class ListBankStatementsHandler : IQueryHandler<ListBankStatements>
{
    public string QueryType => "Treasury.ListBankStatements";

    public async Task<string> HandleAsync(ListBankStatements query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var full = await QueryPermissions.HasAsync(context, AccountNumbers.FullPermission, cancellationToken).ConfigureAwait(false);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.statement_id, s.bank_account_id, b.bank_code, b.account_number, s.period_from, s.period_to, s.opening_balance, s.closing_balance, f.file_name, coalesce(u.display_name, u.email), s.imported_at,
                   (SELECT count(*) FROM fin.bank_statement_line l WHERE l.statement_id = s.statement_id)::int,
                   (SELECT count(*) FROM fin.bank_statement_line l WHERE l.statement_id = s.statement_id AND l.status = 'UNMATCHED')::int, b.alias
            FROM fin.bank_statement s
            JOIN fin.bank_account b ON b.bank_account_id = s.bank_account_id
            JOIN fin.bank_statement_file f ON f.statement_id = s.statement_id
            LEFT JOIN iam.user u ON u.user_id = s.imported_by
            WHERE s.company_id = @c AND (CAST(@b AS uuid) IS NULL OR s.bank_account_id = CAST(@b AS uuid))
            ORDER BY s.period_to DESC, s.imported_at DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new BankStatementView(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), AccountNumbers.Show(r.GetString(3), full), r.Date(4), r.Date(5), r.GetDecimal(6), r.GetDecimal(7), r.GetString(8),
                r.NullableString(9), r.Utc(10), r.GetInt32(11), r.GetInt32(12), r.NullableString(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("b", query.BankAccountId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new BankStatementList(items, query.Limit, query.Offset));
    }
}

public sealed record ListBankStatementLines(Guid CompanyId, Guid SessionId, Guid? StatementId = null, Guid? BankAccountId = null, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record BankStatementLineView(
    Guid LineId, Guid StatementId, Guid BankAccountId, DateOnly ValueDate, string Direction, decimal Amount, string? BankReference, string Description, int Occurrence, string Status,
    Guid? MatchedPaymentId, string? MatchedPaymentNo, Guid? ChargeEventId, long Version);

public sealed record BankStatementLineList(IReadOnlyList<BankStatementLineView> Items, int Limit, int Offset);

[RequiresPermission("bank:read")]
public sealed class ListBankStatementLinesHandler : IQueryHandler<ListBankStatementLines>
{
    public string QueryType => "Treasury.ListBankStatementLines";

    public async Task<string> HandleAsync(ListBankStatementLines query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_id, l.statement_id, l.bank_account_id, l.value_date, l.direction, l.amount, l.bank_reference, l.description, l.occurrence, l.status,
                   l.matched_payment_id, p.payment_no, l.charge_event_id, l.version
            FROM fin.bank_statement_line l
            LEFT JOIN fin.payment p ON p.payment_id = l.matched_payment_id
            WHERE l.company_id = @c
              AND (CAST(@s AS uuid) IS NULL OR l.statement_id = CAST(@s AS uuid))
              AND (CAST(@b AS uuid) IS NULL OR l.bank_account_id = CAST(@b AS uuid))
              AND (CAST(@status AS text) IS NULL OR l.status = CAST(@status AS text))
            ORDER BY l.value_date, l.line_id
            LIMIT @limit OFFSET @offset
            """,
            r => new BankStatementLineView(
                r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.Date(3), r.GetString(4), r.GetDecimal(5), r.NullableString(6), r.GetString(7), r.GetInt32(8), r.GetString(9),
                r.NullableGuid(10), r.NullableString(11), r.NullableGuid(12), r.GetInt64(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.StatementId),
            ("b", query.BankAccountId),
            ("status", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new BankStatementLineList(items, query.Limit, query.Offset));
    }
}

// E-FIS1b-01-9: the released customer refunds waiting for their DEBIT line of the statement, read with bank:read by who matches.
public sealed record ListRefundsToMatch(Guid CompanyId, Guid SessionId, Guid? BankAccountId = null) : IQuery;

public sealed record RefundToMatch(Guid RefundId, string RefundNo, string CustomerName, Guid BankAccountId, string Method, decimal Amount, string? Reference, DateOnly RefundDate, long Version);

public sealed record RefundToMatchList(IReadOnlyList<RefundToMatch> Items);

[RequiresPermission("bank:read")]
public sealed class ListRefundsToMatchHandler : IQueryHandler<ListRefundsToMatch>
{
    public string QueryType => "Treasury.ListRefundsToMatch";

    public async Task<string> HandleAsync(ListRefundsToMatch query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT f.refund_id, f.refund_no, p.legal_name, f.bank_account_id, f.method, f.amount::numeric(19,2), f.reference, f.refund_date, f.version
            FROM fin.customer_refund f JOIN md.party p ON p.party_id = f.party_id
            WHERE f.company_id = @c AND f.status = 'RELEASED' AND (CAST(@b AS uuid) IS NULL OR f.bank_account_id = CAST(@b AS uuid))
            ORDER BY f.refund_date, f.refund_no LIMIT 500
            """,
            r => new RefundToMatch(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetDecimal(5), r.NullableString(6), r.Date(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("b", query.BankAccountId)).ConfigureAwait(false);
        return ApiJson.Serialize(new RefundToMatchList(items));
    }
}

