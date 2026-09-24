using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.SupplierInvoices;

internal sealed record InvoiceHeader(Guid Id, Guid PartyId, string FiscalNumber, DateOnly DocDate, string Status, string AccountingStatus, Guid CreatedBy, long Version);

internal static partial class SupplierInvoiceStore
{
    public const string Aggregate = "SupplierInvoice";

    [GeneratedRegex("^(B[0-9]{10}|E[0-9]{12})$", RegexOptions.None, 1000)]
    public static partial Regex FiscalNumberFormat();

    public static async Task<InvoiceHeader> LockAsync(CommandContext context, Guid siId, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT si_id, party_id, supplier_fiscal_number, doc_date, document_status::text, accounting_status::text, created_by, version
            FROM pur.supplier_invoice WHERE company_id = @c AND si_id = @s FOR UPDATE
            """,
            ("c", context.CompanyId),
            ("s", siId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ProcurementErrors.InvoiceNotFound, "The supplier invoice does not exist.");
        }

        var header = new InvoiceHeader(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4), reader.GetString(5), reader.GetGuid(6), reader.GetInt64(7));
        return header.Version == expectedVersion
            ? header
            : throw new DomainException(ProcurementErrors.VersionConflict, $"The invoice is at version {header.Version}, not {expectedVersion}.");
    }

    public static async Task<IReadOnlyList<SupplierInvoiceLine>> LinesAsync(CommandContext context, Guid siId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT si_line_id, line_no, line_kind, po_line_id, qty, unit_price, net_amount FROM pur.supplier_invoice_line WHERE si_id = @s ORDER BY po_line_id",
            ("s", siId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<SupplierInvoiceLine>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new SupplierInvoiceLine(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetGuid(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6)));
        }

        return lines;
    }

    /// <summary>Status change with its event and ADR-027 history row; <paramref name="extraSet"/> is constant SQL.</summary>
    public static async Task<long> TransitionAsync(
        CommandContext context,
        InvoiceHeader header,
        string toStatus,
        string commandType,
        string eventType,
        object payload,
        bool publish,
        CancellationToken cancellationToken,
        string? reason = null,
        string extraSet = "",
        params (string Name, object? Value)[] extraParameters)
    {
        var newVersion = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, header.Id, newVersion, JsonSerializer.Serialize(payload), publish),
            cancellationToken).ConfigureAwait(false);
        if (toStatus != header.Status)
        {
            await context.AppendStateAsync(Aggregate, header.Id, "DOCUMENT", header.Status, toStatus, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        }

        var parameters = new List<(string Name, object? Value)> { ("status", toStatus), ("version", newVersion), ("id", header.Id) };
        parameters.AddRange(extraParameters);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.supplier_invoice SET document_status = CAST(@status AS pur.si_status), version = @version" + extraSet + " WHERE si_id = @id",
            cancellationToken,
            [.. parameters]).ConfigureAwait(false);
        return newVersion;
    }
}

[RequiresPermission("supplier_invoice:register")]
public sealed class RegisterSupplierInvoiceHandler : ICommandHandler<RegisterSupplierInvoice>
{
    public string CommandType => "Procurement.RegisterSupplierInvoice";

    public async Task<string> HandleAsync(RegisterSupplierInvoice command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var fiscalNumber = (command.SupplierFiscalNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (!SupplierInvoiceStore.FiscalNumberFormat().IsMatch(fiscalNumber))
        {
            throw new DomainException(ProcurementErrors.FiscalNumberInvalid, "The supplier fiscal number must be an NCF (B + 10 digits) or an e-NCF (E + 12 digits).");
        }

        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        if (command.DocDate > today || command.DueDate < command.DocDate)
        {
            throw new DomainException(ProcurementErrors.DateInvalid, "The invoice date cannot be in the future and the due date cannot precede it.");
        }

        if (command.Lines is null || command.Lines.Count == 0)
        {
            throw new DomainException(ProcurementErrors.LinesRequired, "An invoice needs at least one line.");
        }

        if (command.Lines.Select(l => l.PurchaseOrderLineId).Distinct().Count() != command.Lines.Count)
        {
            throw new DomainException(ProcurementErrors.DuplicateLine, "Each PO line can appear only once per invoice.");
        }

        await using (var supplier = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT status = 'ACTIVE' AND is_supplier FROM md.party WHERE company_id = @c AND party_id = @p",
            ("c", context.CompanyId),
            ("p", command.PartyId)))
        {
            if (await supplier.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new DomainException(ProcurementErrors.SupplierNotActive, "The supplier does not exist or is not ACTIVE.");
            }
        }

        await using (var used = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT EXISTS (SELECT 1 FROM pur.supplier_invoice WHERE company_id = @c AND party_id = @p AND supplier_fiscal_number = @n AND document_status <> 'VOIDED')",
            ("c", context.CompanyId),
            ("p", command.PartyId),
            ("n", fiscalNumber)))
        {
            if (await used.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                throw new DomainException(ProcurementErrors.FiscalNumberUsed, $"Fiscal number {fiscalNumber} is already registered for this supplier.");
            }
        }

        foreach (var line in command.Lines)
        {
            await SupplierInvoiceLineHandlers.For(line.LineKind).ValidateAsync(context, command.PartyId, line, cancellationToken).ConfigureAwait(false);
        }

        var nets = command.Lines.Select(l => decimal.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero)).ToList();
        var total = nets.Sum();
        var creator = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var siId = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierInvoiceRegistered",
                1,
                SupplierInvoiceStore.Aggregate,
                siId,
                1,
                JsonSerializer.Serialize(new
                {
                    siId,
                    partyId = command.PartyId,
                    supplierFiscalNumber = fiscalNumber,
                    docDate = command.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    dueDate = command.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    totalAmount = total.ToString(CultureInfo.InvariantCulture),
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(SupplierInvoiceStore.Aggregate, siId, "DOCUMENT", null, SupplierInvoiceStatus.Draft, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.supplier_invoice (si_id, company_id, party_id, supplier_fiscal_number, doc_date, due_date, document_status, accounting_status, total_amount, created_by, version)
            VALUES (@id, @c, @party, @ncf, @doc, @due, 'DRAFT', 'NOT_POSTED', @total, @by, 1)
            """,
            cancellationToken,
            ("id", siId),
            ("c", context.CompanyId),
            ("party", command.PartyId),
            ("ncf", fiscalNumber),
            ("doc", command.DocDate),
            ("due", command.DueDate),
            ("total", total),
            ("by", creator)).ConfigureAwait(false);

        for (var i = 0; i < command.Lines.Count; i++)
        {
            var line = command.Lines[i];
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.supplier_invoice_line (si_line_id, company_id, si_id, line_no, line_kind, po_line_id, qty, unit_price, net_amount)
                VALUES (@id, @c, @si, @no, @kind, @pol, @qty, @price, @net)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("si", siId),
                ("no", i + 1),
                ("kind", line.LineKind),
                ("pol", line.PurchaseOrderLineId),
                ("qty", line.Quantity),
                ("price", line.UnitPrice),
                ("net", nets[i])).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { supplierInvoiceId = siId, status = SupplierInvoiceStatus.Draft, totalAmount = total.ToString(CultureInfo.InvariantCulture), version = 1 });
    }
}

[RequiresPermission("supplier_invoice:match")]
public sealed class MatchSupplierInvoiceHandler : ICommandHandler<MatchSupplierInvoice>
{
    public string CommandType => "Procurement.MatchSupplierInvoice";

    public async Task<string> HandleAsync(MatchSupplierInvoice command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await SupplierInvoiceStore.LockAsync(context, command.SupplierInvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (header.Status is not (SupplierInvoiceStatus.Draft or SupplierInvoiceStatus.MatchException))
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The invoice is {header.Status}; only DRAFT or MATCH_EXCEPTION invoices are matched.");
        }

        var policy = await PolicyResolver.ResolveAsync(context, PolicyCodes.Purchasing, header.DocDate, cancellationToken).ConfigureAwait(false);
        var results = new List<LineMatch>();
        foreach (var line in await SupplierInvoiceStore.LinesAsync(context, header.Id, cancellationToken).ConfigureAwait(false))
        {
            var match = await SupplierInvoiceLineHandlers.For(line.LineKind).MatchAsync(context, line, policy, cancellationToken).ConfigureAwait(false);
            results.Add(match);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.match_result (si_line_id, company_id, qty_available_to_invoice, qty_diff, price_diff, amount_diff, qty_exceeds, within_tolerance, policy_version_id, evaluated_at)
                VALUES (@line, @c, @available, @qtyDiff, @priceDiff, @amountDiff, @exceeds, @within, @policy, @at)
                ON CONFLICT (si_line_id) DO UPDATE SET
                  qty_available_to_invoice = EXCLUDED.qty_available_to_invoice, qty_diff = EXCLUDED.qty_diff, price_diff = EXCLUDED.price_diff,
                  amount_diff = EXCLUDED.amount_diff, qty_exceeds = EXCLUDED.qty_exceeds, within_tolerance = EXCLUDED.within_tolerance,
                  policy_version_id = EXCLUDED.policy_version_id, evaluated_at = EXCLUDED.evaluated_at
                """,
                cancellationToken,
                ("line", match.LineId),
                ("c", context.CompanyId),
                ("available", match.QtyAvailable),
                ("qtyDiff", match.QtyDiff),
                ("priceDiff", match.PriceDiff),
                ("amountDiff", match.AmountDiff),
                ("exceeds", match.QtyExceeds),
                ("within", match.WithinTolerance),
                ("policy", policy.PolicyVersionId),
                ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        }

        var matched = results.All(r => r.WithinTolerance);
        var status = matched ? SupplierInvoiceStatus.Matched : SupplierInvoiceStatus.MatchException;
        var version = await SupplierInvoiceStore.TransitionAsync(
            context,
            header,
            status,
            CommandType,
            matched ? "SupplierInvoiceMatched" : "MatchExceptionRaised",
            new
            {
                siId = header.Id,
                status,
                policyVersionId = policy.PolicyVersionId,
                lines = results.Select(r => new
                {
                    siLineId = r.LineId,
                    qtyAvailable = r.QtyAvailable.ToString(CultureInfo.InvariantCulture),
                    qtyDiff = r.QtyDiff.ToString(CultureInfo.InvariantCulture),
                    priceDiff = r.PriceDiff.ToString(CultureInfo.InvariantCulture),
                    amountDiff = r.AmountDiff.ToString(CultureInfo.InvariantCulture),
                    qtyExceeds = r.QtyExceeds,
                    withinTolerance = r.WithinTolerance,
                }),
            },
            publish: !matched,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, status, qtyExceeds = results.Any(r => r.QtyExceeds), version });
    }
}

[RequiresPermission("match_exception:approve", StepUp = true)]
public sealed class ApproveMatchExceptionHandler : ICommandHandler<ApproveMatchException>
{
    public string CommandType => "Procurement.ApproveMatchException";

    public async Task<string> HandleAsync(ApproveMatchException command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var header = await SupplierInvoiceStore.LockAsync(context, command.SupplierInvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (header.Status != SupplierInvoiceStatus.MatchException)
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The invoice is {header.Status}; only MATCH_EXCEPTION invoices can be approved.");
        }

        var approver = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == header.CreatedBy)
        {
            throw new DomainException(ProcurementErrors.ApproverIsCreator, "The person who registered the invoice cannot approve its exception.");
        }

        await using (var exceeds = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT EXISTS (SELECT 1 FROM pur.match_result m JOIN pur.supplier_invoice_line l ON l.si_line_id = m.si_line_id WHERE l.si_id = @s AND m.qty_exceeds)",
            ("s", header.Id)))
        {
            if (await exceeds.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                throw new DomainException(ProcurementErrors.QtyExceptionNotApprovable, "The invoice bills more than was received; correct the receipt and match again (E-PR13-1).");
            }
        }

        var version = await SupplierInvoiceStore.TransitionAsync(
            context,
            header,
            SupplierInvoiceStatus.Matched,
            CommandType,
            "MatchExceptionApproved",
            new { siId = header.Id, approvedBy = approver, reason },
            publish: true,
            cancellationToken,
            reason,
            ", exception_approved_by = @approver",
            ("approver", approver)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, status = SupplierInvoiceStatus.Matched, version });
    }
}

[RequiresPermission("supplier_invoice:void")]
public sealed class VoidSupplierInvoiceHandler : ICommandHandler<VoidSupplierInvoice>
{
    public string CommandType => "Procurement.VoidSupplierInvoice";

    public async Task<string> HandleAsync(VoidSupplierInvoice command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var header = await SupplierInvoiceStore.LockAsync(context, command.SupplierInvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (header.Status is not (SupplierInvoiceStatus.Draft or SupplierInvoiceStatus.MatchException or SupplierInvoiceStatus.Matched)
            || header.AccountingStatus is not ("NOT_POSTED" or "POSTING_BLOCKED"))
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The invoice is {header.Status}/{header.AccountingStatus}; only unposted invoices can be voided.");
        }

        // E-PR13b-1: a POSTING_BLOCKED invoice never posted anything; voided, its accounting status is NOT_POSTED again.
        var version = await SupplierInvoiceStore.TransitionAsync(
            context,
            header,
            SupplierInvoiceStatus.Voided,
            CommandType,
            "SupplierInvoiceVoided",
            new { siId = header.Id, reason, previousAccountingStatus = header.AccountingStatus },
            publish: true,
            cancellationToken,
            reason,
            ", accounting_status = 'NOT_POSTED'").ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierInvoiceId = header.Id, status = SupplierInvoiceStatus.Voided, version });
    }
}
