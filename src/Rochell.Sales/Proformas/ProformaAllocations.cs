using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Receipts;

namespace Rochell.Sales.Proformas;

public static class AllocationErrors
{
    public const string ExceedsAvailable = "ALLOCATION_EXCEEDS_AVAILABLE";
    public const string ExceedsBalance = "ALLOCATION_EXCEEDS_BALANCE";
    public const string ProformaNotOpen = "PROFORMA_NOT_OPEN";
    public const string OtherCustomer = "PROFORMA_OF_ANOTHER_CUSTOMER";
    public const string NotFound = "ALLOCATION_NOT_FOUND";
}

public sealed record ProformaAllocationInput(Guid ProformaId, decimal Amount);

/// <summary>
/// E-FIS1b-4, E-FIS1b-01-4 (option A): Cobros allocates a receipt to open proformas of its customer, each up to its balance (its
/// total, or its net when it collects without ITBIS). Nothing posts: the money stays unapplied on the receipt, only kept from
/// being applied or allocated twice, until the invoice of the proforma is issued.
/// </summary>
public sealed record AllocateReceiptToProformas(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, long ExpectedVersion, IReadOnlyList<ProformaAllocationInput> Allocations) : ICommand;

/// <summary>Releases one whole allocation (its ReceiptAllocated event), with a reason.</summary>
public sealed record ReleaseProformaAllocation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, Guid AllocationEventId, string Reason) : ICommand;

/// <summary>Shared reads, locks and writes of the allocations. Lock order: proformas (by id) → invoices → AR documents → receipt.</summary>
internal static class Allocations
{
    public sealed record Proforma(Guid ProformaId, Guid PartyId, string ProformaNo, string Status, decimal Net, decimal Collectible, decimal Allocated, long Version);

    /// <summary>A live allocation: never released (no mirror row).</summary>
    public sealed record Live(Guid AllocationId, Guid ReceiptId, Guid ProformaId, string ProformaNo, decimal Amount, Guid EventId);

    public static async Task<List<Proforma>> LockProformasAsync(CommandContext context, IEnumerable<Guid> proformaIds, CancellationToken cancellationToken)
    {
        var proformas = new List<Proforma>();
        foreach (var id in proformaIds.Distinct().Order())
        {
            proformas.Add(await Reading.SingleOrDefaultAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT proforma_id, party_id, proforma_no, status, net_total::numeric(19,2), (CASE WHEN collects_itbis THEN total ELSE net_total END)::numeric(19,2),
                       allocated_amount::numeric(19,2), version
                FROM sal.proforma WHERE company_id = @c AND proforma_id = @p FOR UPDATE
                """,
                r => new Proforma(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetInt64(7)),
                cancellationToken,
                ("c", context.CompanyId),
                ("p", id)).ConfigureAwait(false)
                ?? throw new DomainException(SalesErrors.NotFound, "The proforma does not exist."));
        }

        return proformas;
    }

    private const string LiveSelect = """
        SELECT x.allocation_id, x.receipt_id, x.proforma_id, p.proforma_no, x.amount::numeric(19,2), x.event_id
        FROM fin.proforma_allocation x JOIN sal.proforma p ON p.proforma_id = x.proforma_id
        WHERE x.company_id = @c AND x.reverses_allocation_id IS NULL
          AND NOT EXISTS (SELECT 1 FROM fin.proforma_allocation u WHERE u.reverses_allocation_id = x.allocation_id)
        """;

    private static Live Map(System.Data.Common.DbDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetDecimal(4), r.GetGuid(5));

    public static Task<List<Live>> LiveOfReceiptAsync(CommandContext context, Guid receiptId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection, context.Transaction, LiveSelect + " AND x.receipt_id = @r ORDER BY x.event_id, x.allocation_id", Map, cancellationToken,
            ("c", context.CompanyId), ("r", receiptId));

    /// <summary>The live allocations to these proformas, oldest receipt first: the order in which an invoice takes them.</summary>
    public static Task<List<Live>> LiveOfProformasAsync(CommandContext context, IEnumerable<Guid> proformaIds, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection, context.Transaction, LiveSelect + " AND x.proforma_id = ANY (@ids) ORDER BY x.allocation_id", Map, cancellationToken,
            ("c", context.CompanyId), ("ids", proformaIds.Distinct().ToArray()));

    /// <summary>
    /// Releases live allocations of one receipt: the mirror rows, the proformas' and the receipt's allocated amounts back, and a
    /// ReceiptAllocationReleased event with the receipt's next version. The caller holds the locks of the proformas and the receipt.
    /// </summary>
    public static async Task<Guid> ReleaseAsync(
        CommandContext context, Guid receiptId, string receiptNo, IReadOnlyList<Live> allocations, long version, string reason, string commandType, Guid? causation,
        CancellationToken cancellationToken)
    {
        var amount = allocations.Sum(a => a.Amount);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptAllocationReleased",
                1,
                Receipting.Aggregate,
                receiptId,
                version,
                JsonSerializer.Serialize(new
                {
                    receiptId,
                    receiptNo,
                    proformas = allocations.Select(a => new { proformaId = a.ProformaId, proformaNo = a.ProformaNo, amount = Receipting.Money(a.Amount) }),
                    amount = Receipting.Money(amount),
                    reason,
                    command = commandType,
                }),
                Publish: true,
                BusinessDate: SalesSql.Today(context),
                CausationId: causation),
            cancellationToken).ConfigureAwait(false);
        foreach (var a in allocations)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.proforma_allocation (allocation_id, company_id, receipt_id, proforma_id, amount, event_id, reverses_allocation_id)
                VALUES (@id, @c, @r, @p, @a, @e, @original)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("r", receiptId),
                ("p", a.ProformaId),
                ("a", a.Amount),
                ("e", eventId),
                ("original", a.AllocationId)).ConfigureAwait(false);
            await MoveAllocatedAsync(context, a.ProformaId, -a.Amount, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.receipt SET allocated_amount = allocated_amount - @a, version = @v WHERE receipt_id = @r", cancellationToken,
            ("a", amount), ("v", version), ("r", receiptId)).ConfigureAwait(false);
        return eventId;
    }

    public static Task MoveAllocatedAsync(CommandContext context, Guid proformaId, decimal delta, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE sal.proforma SET allocated_amount = allocated_amount + @d, version = version + 1 WHERE proforma_id = @p", cancellationToken,
            ("d", delta), ("p", proformaId));
}

[RequiresPermission("receipt:apply")]
public sealed class AllocateReceiptToProformasHandler : ICommandHandler<AllocateReceiptToProformas>
{
    public string CommandType => "Sales.AllocateReceiptToProformas";

    public async Task<string> HandleAsync(AllocateReceiptToProformas command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var input = command.Allocations ?? [];
        if (input.Count == 0 || input.Select(a => a.ProformaId).Distinct().Count() != input.Count)
        {
            throw new DomainException(SalesErrors.LinesRequired, "An allocation names one or more proformas, each once.");
        }

        foreach (var a in input)
        {
            SalesSql.Positive(a.Amount, 2, "Each allocated amount");
        }

        var proformas = await Allocations.LockProformasAsync(context, input.Select(a => a.ProformaId), cancellationToken).ConfigureAwait(false);
        var receipt = await Receipting.LockAsync(context, command.ReceiptId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (receipt.Status != "RECORDED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The receipt is {receipt.Status}.");
        }

        var total = input.Sum(a => a.Amount);
        var available = receipt.Unapplied - receipt.Allocated;
        if (total > available)
        {
            throw new DomainException(AllocationErrors.ExceedsAvailable, $"{Receipting.Money(total)} exceeds the {Receipting.Money(available)} of {receipt.No} that is neither applied nor allocated.");
        }

        foreach (var a in input)
        {
            var proforma = proformas.Single(p => p.ProformaId == a.ProformaId);
            if (proforma.PartyId != receipt.PartyId)
            {
                throw new DomainException(AllocationErrors.OtherCustomer, $"{proforma.ProformaNo} is another customer's proforma.");
            }

            if (proforma.Status != "OPEN")
            {
                throw new DomainException(AllocationErrors.ProformaNotOpen, $"{proforma.ProformaNo} is {proforma.Status}; receipts are allocated to OPEN proformas.");
            }

            if (a.Amount > proforma.Collectible - proforma.Allocated)
            {
                throw new DomainException(
                    AllocationErrors.ExceedsBalance, $"{Receipting.Money(a.Amount)} exceeds the {Receipting.Money(proforma.Collectible - proforma.Allocated)} still to collect on {proforma.ProformaNo}.");
            }
        }

        var version = receipt.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptAllocated",
                1,
                Receipting.Aggregate,
                command.ReceiptId,
                version,
                JsonSerializer.Serialize(new
                {
                    receiptId = command.ReceiptId,
                    receiptNo = receipt.No,
                    proformas = input.Select(a => new { proformaId = a.ProformaId, proformaNo = proformas.Single(p => p.ProformaId == a.ProformaId).ProformaNo, amount = Receipting.Money(a.Amount) }),
                    amount = Receipting.Money(total),
                }),
                Publish: true,
                BusinessDate: SalesSql.Today(context)),
            cancellationToken).ConfigureAwait(false);
        foreach (var a in input)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO fin.proforma_allocation (allocation_id, company_id, receipt_id, proforma_id, amount, event_id) VALUES (@id, @c, @r, @p, @a, @e)",
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("r", command.ReceiptId),
                ("p", a.ProformaId),
                ("a", a.Amount),
                ("e", eventId)).ConfigureAwait(false);
            await Allocations.MoveAllocatedAsync(context, a.ProformaId, a.Amount, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.receipt SET allocated_amount = allocated_amount + @a, version = @v WHERE receipt_id = @r", cancellationToken,
            ("a", total), ("v", version), ("r", command.ReceiptId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            receiptId = command.ReceiptId,
            allocationEventId = eventId,
            allocated = Receipting.Money(receipt.Allocated + total),
            available = Receipting.Money(available - total),
            version,
        });
    }
}

[RequiresPermission("receipt:apply")]
public sealed class ReleaseProformaAllocationHandler : ICommandHandler<ReleaseProformaAllocation>
{
    public string CommandType => "Sales.ReleaseProformaAllocation";

    public async Task<string> HandleAsync(ReleaseProformaAllocation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Receipting.Reason(command.Reason);

        // The proformas of the event are locked before the receipt; an allocation released meanwhile leaves nothing to release.
        var before = (await Allocations.LiveOfReceiptAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false)).Where(a => a.EventId == command.AllocationEventId).ToList();
        await Allocations.LockProformasAsync(context, before.Select(a => a.ProformaId), cancellationToken).ConfigureAwait(false);
        var receipt = await Receipting.LockAsync(context, command.ReceiptId, null, cancellationToken).ConfigureAwait(false);
        var live = (await Allocations.LiveOfReceiptAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false)).Where(a => a.EventId == command.AllocationEventId).ToList();
        if (live.Count == 0 || !live.Select(a => a.AllocationId).Order().SequenceEqual(before.Select(a => a.AllocationId).Order()))
        {
            throw new DomainException(AllocationErrors.NotFound, "The receipt has no live allocation with that event.");
        }

        var version = receipt.Version + 1;
        var eventId = await Allocations.ReleaseAsync(context, command.ReceiptId, receipt.No, live, version, reason, CommandType, null, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { receiptId = command.ReceiptId, releaseEventId = eventId, released = Receipting.Money(live.Sum(a => a.Amount)), version });
    }
}
