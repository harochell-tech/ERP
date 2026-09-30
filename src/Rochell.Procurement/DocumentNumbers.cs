using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Procurement;

/// <summary>
/// E-UX4-5: purchase orders and goods receipts are numbered OC-YYYY-000123 / RM-YYYY-000045 per company and year — the next number
/// after the highest of that year, under an advisory lock of (company, prefix, year), the way payments get PAG-… (E-VS2-03-7).
/// Numbers issued before (8 hex characters, E-PR08-3) keep theirs and do not count.
/// </summary>
internal static class DocumentNumbers
{
    public const string PurchaseOrder = "OC";
    public const string GoodsReceipt = "RM";

    public static async Task<string> NextAsync(CommandContext context, string prefix, int year, CancellationToken cancellationToken)
    {
        var (table, column) = prefix switch
        {
            PurchaseOrder => ("pur.purchase_order", "po_no"),
            GoodsReceipt => ("pur.goods_receipt", "gr_no"),
            _ => throw new ArgumentOutOfRangeException(nameof(prefix), prefix, "Unknown document prefix."),
        };
        var stem = string.Create(CultureInfo.InvariantCulture, $"{prefix}-{year:D4}-");
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))",
            cancellationToken,
            ("k", $"{prefix}-no:{context.CompanyId}:{year}")).ConfigureAwait(false);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            $"SELECT max(substring({column} FROM {stem.Length + 1})::int) FROM {table} WHERE company_id = @c AND {column} ~ ('^' || @stem || '[0-9]{{6,7}}$')",
            ("c", context.CompanyId),
            ("stem", stem));
        var last = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int n ? n : 0;
        return stem + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
    }
}

/// <summary>0.00: adding it keeps two decimals in the JSON ("0.00"), where a bare 0m would print "0".</summary>
internal static class Money
{
    public static readonly decimal Zero = new(0, 0, 0, false, 2);
}
