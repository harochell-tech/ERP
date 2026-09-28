using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Queries;

// E-VS3-07-14: receipts and deposit slips, read with sales:read.

public sealed record ListReceipts(
    Guid CompanyId, Guid SessionId, Guid? PartyId = null, string? Status = null, string? ApplicationStatus = null, string? BankStatus = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record ReceiptSummary(
    Guid ReceiptId, string ReceiptNo, Guid PartyId, string CustomerName, string Method, decimal Amount, DateOnly ReceiptDate, DateOnly ValueDate, Guid? BankAccountId, string? Reference,
    string? ChequeBank, string? ChequeNo, DateOnly? ChequeDate, string Status, string ApplicationStatus, string BankStatus, decimal Unapplied, Guid? DepositId, string? DepositNo, long Version);

public sealed record ReceiptList(IReadOnlyList<ReceiptSummary> Items, int Limit, int Offset);

internal static class ReceiptReading
{
    public const string Select = """
        SELECT r.receipt_id, r.receipt_no, r.party_id, p.legal_name, r.method, r.amount::numeric(19,2), r.receipt_date, r.value_date, r.bank_account_id, r.reference,
               r.cheque_bank, r.cheque_no, r.cheque_date, r.status, r.application_status, r.bank_status, r.unapplied_amount::numeric(19,2), r.deposit_id, d.deposit_no, r.version
        FROM fin.receipt r
        JOIN md.party p ON p.party_id = r.party_id
        LEFT JOIN fin.receipt_deposit d ON d.deposit_id = r.deposit_id
        """;

    public static ReceiptSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.Date(6), r.Date(7), r.NullableGuid(8), r.NullableString(9),
            r.NullableString(10), r.NullableString(11), r.IsDBNull(12) ? null : r.Date(12), r.GetString(13), r.GetString(14), r.GetString(15), r.GetDecimal(16), r.NullableGuid(17),
            r.NullableString(18), r.GetInt64(19));
}

[RequiresPermission("sales:read")]
public sealed class ListReceiptsHandler : IQueryHandler<ListReceipts>
{
    public string QueryType => "Sales.ListReceipts";

    public async Task<string> HandleAsync(ListReceipts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            ReceiptReading.Select + """
             WHERE r.company_id = @c AND (CAST(@p AS uuid) IS NULL OR r.party_id = CAST(@p AS uuid)) AND (CAST(@s AS text) IS NULL OR r.status = CAST(@s AS text))
              AND (CAST(@a AS text) IS NULL OR r.application_status = CAST(@a AS text)) AND (CAST(@b AS text) IS NULL OR r.bank_status = CAST(@b AS text))
            ORDER BY r.receipt_no DESC
            LIMIT @limit OFFSET @offset
            """,
            ReceiptReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId),
            ("s", query.Status),
            ("a", query.ApplicationStatus),
            ("b", query.BankStatus),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new ReceiptList(items, query.Limit, query.Offset));
    }
}

public sealed record GetReceipt(Guid CompanyId, Guid SessionId, Guid ReceiptId) : IQuery;

/// <summary>An application row; an unapply shows as its own row with <c>ReversesApplicationId</c> and <c>Live</c> false on both.</summary>
public sealed record ReceiptApplicationView(Guid ApplicationId, Guid EventId, Guid InvoiceId, string InvoiceNo, decimal Amount, DateTime At, Guid? ReversesApplicationId, bool Live);

public sealed record MatchedLineView(Guid LineId, DateOnly ValueDate, string Direction, decimal Amount, string? BankReference, string Description);

public sealed record ReceiptDetail(
    ReceiptSummary Header, string? RecordedBy, string? ClosingReason, IReadOnlyList<ReceiptApplicationView> Applications, IReadOnlyList<MatchedLineView> MatchedLines,
    IReadOnlyList<StateChange> History);

[RequiresPermission("sales:read")]
public sealed class GetReceiptHandler : IQueryHandler<GetReceipt>
{
    public string QueryType => "Sales.GetReceipt";

    private sealed record Extra(string? RecordedBy, string? ClosingReason);

    public async Task<string> HandleAsync(GetReceipt query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, ReceiptReading.Select + " WHERE r.company_id = @c AND r.receipt_id = @r", ReceiptReading.Map, cancellationToken,
            ("c", context.CompanyId), ("r", query.ReceiptId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The receipt does not exist.");
        var extra = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT u.email, r.closing_reason FROM fin.receipt r LEFT JOIN iam.user u ON u.user_id = r.recorded_by WHERE r.receipt_id = @r",
            r => new Extra(r.NullableString(0), r.NullableString(1)),
            cancellationToken,
            ("r", query.ReceiptId)).ConfigureAwait(false))!;
        var applications = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.application_id, x.event_id, i.invoice_id, i.invoice_no, x.amount::numeric(19,2), e.occurred_at, x.reverses_application_id,
                   x.reverses_application_id IS NULL AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = x.application_id)
            FROM fin.ar_application x
            JOIN sal.invoice i ON i.ar_doc_id = x.ar_doc_id
            JOIN core.domain_event e ON e.company_id = x.company_id AND e.event_id = x.event_id
            WHERE x.receipt_id = @r ORDER BY e.occurred_at, x.application_id
            """,
            r => new ReceiptApplicationView(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetDecimal(4), r.GetFieldValue<DateTime>(5), r.NullableGuid(6), r.GetBoolean(7)),
            cancellationToken,
            ("r", query.ReceiptId)).ConfigureAwait(false);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_id, l.value_date, l.direction, l.amount::numeric(19,2), l.bank_reference, l.description
            FROM fin.bank_statement_line l
            WHERE l.matched_receipt_id = @r OR l.matched_deposit_id = (SELECT deposit_id FROM fin.receipt WHERE receipt_id = @r)
            ORDER BY l.value_date
            """,
            r => new MatchedLineView(r.GetGuid(0), r.Date(1), r.GetString(2), r.GetDecimal(3), r.NullableString(4), r.GetString(5)),
            cancellationToken,
            ("r", query.ReceiptId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "Receipt", query.ReceiptId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new ReceiptDetail(header, extra.RecordedBy, extra.ClosingReason, applications, lines, history));
    }
}

public sealed record ListDeposits(Guid CompanyId, Guid SessionId, Guid? BankAccountId = null, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record DepositSummary(Guid DepositId, string DepositNo, Guid BankAccountId, string BankCode, string AccountNumber, DateOnly DepositDate, decimal Total, string Status, int Receipts, long Version);

public sealed record DepositList(IReadOnlyList<DepositSummary> Items, int Limit, int Offset);

internal static class DepositReading
{
    public const string Select = """
        SELECT d.deposit_id, d.deposit_no, d.bank_account_id, b.bank_code, b.account_number, d.deposit_date, d.total::numeric(19,2), d.status,
               (SELECT count(*)::int FROM fin.receipt r WHERE r.deposit_id = d.deposit_id), d.version
        FROM fin.receipt_deposit d JOIN fin.bank_account b ON b.bank_account_id = d.bank_account_id
        """;

    /// <summary>The account number always masked here: sales:read does not carry bank_account_number:read.</summary>
    public static DepositSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), AccountNumbers.Show(r.GetString(4), full: false), r.Date(5), r.GetDecimal(6), r.GetString(7), r.GetInt32(8),
            r.GetInt64(9));
}

[RequiresPermission("sales:read")]
public sealed class ListDepositsHandler : IQueryHandler<ListDeposits>
{
    public string QueryType => "Sales.ListDeposits";

    public async Task<string> HandleAsync(ListDeposits query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            DepositReading.Select + """
             WHERE d.company_id = @c AND (CAST(@b AS uuid) IS NULL OR d.bank_account_id = CAST(@b AS uuid)) AND (CAST(@s AS text) IS NULL OR d.status = CAST(@s AS text))
            ORDER BY d.deposit_no DESC
            LIMIT @limit OFFSET @offset
            """,
            DepositReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("b", query.BankAccountId),
            ("s", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new DepositList(items, query.Limit, query.Offset));
    }
}

public sealed record GetDeposit(Guid CompanyId, Guid SessionId, Guid DepositId) : IQuery;

public sealed record DepositDetail(DepositSummary Header, IReadOnlyList<ReceiptSummary> Receipts, IReadOnlyList<MatchedLineView> MatchedLines);

[RequiresPermission("sales:read")]
public sealed class GetDepositHandler : IQueryHandler<GetDeposit>
{
    public string QueryType => "Sales.GetDeposit";

    public async Task<string> HandleAsync(GetDeposit query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, DepositReading.Select + " WHERE d.company_id = @c AND d.deposit_id = @d", DepositReading.Map, cancellationToken,
            ("c", context.CompanyId), ("d", query.DepositId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The deposit does not exist.");
        var receipts = await Reading.ListAsync(
            context.Connection, context.Transaction, ReceiptReading.Select + " WHERE r.deposit_id = @d ORDER BY r.receipt_no", ReceiptReading.Map, cancellationToken,
            ("d", query.DepositId)).ConfigureAwait(false);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT l.line_id, l.value_date, l.direction, l.amount::numeric(19,2), l.bank_reference, l.description FROM fin.bank_statement_line l WHERE l.matched_deposit_id = @d",
            r => new MatchedLineView(r.GetGuid(0), r.Date(1), r.GetString(2), r.GetDecimal(3), r.NullableString(4), r.GetString(5)),
            cancellationToken,
            ("d", query.DepositId)).ConfigureAwait(false);
        return ApiJson.Serialize(new DepositDetail(header, receipts, lines));
    }
}
