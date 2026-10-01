using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Refunds;

// E-FIS1b-8, E-FIS1b-01-9: customer refunds, read with sales:read.

public sealed record ListCustomerRefunds(Guid CompanyId, Guid SessionId, Guid? PartyId = null, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record CustomerRefundSummary(
    Guid RefundId, string RefundNo, Guid PartyId, string CustomerName, Guid ReceiptId, string ReceiptNo, Guid BankAccountId, string? BankAccountAlias, string BankCode, string Method,
    decimal Amount, string? Reference, string Reason, DateOnly? RefundDate, string Status, string? PreparedBy, string? ReleasedBy, string? VoidReason, long Version);

public sealed record CustomerRefundList(IReadOnlyList<CustomerRefundSummary> Items, int Limit, int Offset);

public sealed record GetCustomerRefund(Guid CompanyId, Guid SessionId, Guid RefundId) : IQuery;

public sealed record CustomerRefundDetail(CustomerRefundSummary Header, IReadOnlyList<StateChange> History);

internal static class RefundReading
{
    public const string Select = """
        SELECT f.refund_id, f.refund_no, f.party_id, p.legal_name, f.receipt_id, r.receipt_no, f.bank_account_id, b.alias, b.bank_code, f.method, f.amount::numeric(19,2),
               f.reference, f.reason, f.refund_date, f.status, coalesce(pu.display_name, pu.email), coalesce(ru.display_name, ru.email), f.void_reason, f.version
        FROM fin.customer_refund f
        JOIN md.party p ON p.party_id = f.party_id
        JOIN fin.receipt r ON r.receipt_id = f.receipt_id
        JOIN fin.bank_account b ON b.bank_account_id = f.bank_account_id
        JOIN iam.user pu ON pu.user_id = f.prepared_by
        LEFT JOIN iam.user ru ON ru.user_id = f.released_by
        """;

    public static CustomerRefundSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetGuid(6), r.NullableString(7), r.GetString(8), r.GetString(9), r.GetDecimal(10),
            r.NullableString(11), r.GetString(12), r.IsDBNull(13) ? null : r.Date(13), r.GetString(14), r.NullableString(15), r.NullableString(16), r.NullableString(17), r.GetInt64(18));
}

[RequiresPermission("sales:read")]
public sealed class ListCustomerRefundsHandler : IQueryHandler<ListCustomerRefunds>
{
    public string QueryType => "Sales.ListCustomerRefunds";

    public async Task<string> HandleAsync(ListCustomerRefunds query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            RefundReading.Select + """

            WHERE f.company_id = @c AND (CAST(@p AS uuid) IS NULL OR f.party_id = CAST(@p AS uuid)) AND (CAST(@s AS text) IS NULL OR f.status = CAST(@s AS text))
            ORDER BY f.refund_no DESC LIMIT @limit OFFSET @offset
            """,
            RefundReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId),
            ("s", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new CustomerRefundList(items, query.Limit, query.Offset));
    }
}

[RequiresPermission("sales:read")]
public sealed class GetCustomerRefundHandler : IQueryHandler<GetCustomerRefund>
{
    public string QueryType => "Sales.GetCustomerRefund";

    public async Task<string> HandleAsync(GetCustomerRefund query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, RefundReading.Select + " WHERE f.company_id = @c AND f.refund_id = @id", RefundReading.Map, cancellationToken,
            ("c", context.CompanyId), ("id", query.RefundId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The refund does not exist.");
        var history = await StateHistory.ReadAsync(context, Refunds.Aggregate, query.RefundId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new CustomerRefundDetail(header, history));
    }
}
