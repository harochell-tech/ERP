using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Receipts;

namespace Rochell.Sales.Proformas;

/// <summary>E-FIS1b-10, E-FIS1b-01-11: an OPEN proforma without allocations — an order marked by mistake — is voided with a reason; its delivery returns to normal invoicing.</summary>
public sealed record VoidProforma(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ProformaId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>What happens to proformas when their invoice is issued or voided.</summary>
internal static class ProformaInvoicing
{
    /// <summary>
    /// E-FIS1b-01-7: releases every live allocation of the invoice's proformas and applies it to the new AR document, receipt by
    /// receipt (P-25), up to the invoice's total; the rest stays unapplied on its receipt as the customer's credit balance. The
    /// proformas become INVOICED. Returns what was applied. The caller holds the proformas' and the invoice's locks.
    /// </summary>
    public static async Task<decimal> InheritAsync(
        CommandContext context, PostingEngine engine, Guid invoiceId, string invoiceNo, Guid arDocId, decimal total, IReadOnlyList<Allocations.Proforma> proformas, Guid invoiceEvent,
        string commandType, CancellationToken cancellationToken)
    {
        var live = await Allocations.LiveOfProformasAsync(context, proformas.Select(f => f.ProformaId), cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT 1 FROM fin.ar_document WHERE ar_doc_id = @a FOR UPDATE", cancellationToken, ("a", arDocId)).ConfigureAwait(false);
        var open = total;
        foreach (var group in live.GroupBy(a => a.ReceiptId).OrderBy(g => g.Key))
        {
            var receipt = await Receipting.LockAsync(context, group.Key, null, cancellationToken).ConfigureAwait(false);
            var version = receipt.Version + 1;
            await Allocations.ReleaseAsync(context, group.Key, receipt.No, [.. group], version, $"Invoice {invoiceNo} issued", commandType, invoiceEvent, cancellationToken).ConfigureAwait(false);
            var amount = Math.Min(group.Sum(a => a.Amount), open);
            if (amount > 0m)
            {
                await Receipting.ApplyAsync(context, engine, group.Key, receipt, version + 1, [new Receipting.Target(invoiceId, invoiceNo, arDocId, amount)], invoiceEvent, cancellationToken).ConfigureAwait(false);
                open -= amount;
            }
        }

        await SetStatusAsync(context, proformas, "INVOICED", invoiceId, invoiceEvent, commandType, cancellationToken).ConfigureAwait(false);
        return total - open;
    }

    public static async Task SetStatusAsync(
        CommandContext context, IReadOnlyList<Allocations.Proforma> proformas, string to, Guid? invoiceId, Guid eventId, string commandType, CancellationToken cancellationToken, string? reason = null)
    {
        foreach (var proforma in proformas)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE sal.proforma SET status = @s, invoice_id = @i, void_reason = @r, version = version + 1 WHERE proforma_id = @p",
                cancellationToken,
                ("s", to),
                ("i", invoiceId),
                ("r", reason),
                ("p", proforma.ProformaId)).ConfigureAwait(false);
            await context.AppendStateAsync(Proformas.Aggregate, proforma.ProformaId, "DOCUMENT", proforma.Status, to, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        }
    }
}

[RequiresPermission("proforma:void")]
public sealed class VoidProformaHandler : ICommandHandler<VoidProforma>
{
    public string CommandType => "Sales.VoidProforma";

    public async Task<string> HandleAsync(VoidProforma command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Receipting.Reason(command.Reason);
        var proforma = (await Allocations.LockProformasAsync(context, [command.ProformaId], cancellationToken).ConfigureAwait(false))[0];
        if (proforma.Version != command.ExpectedVersion)
        {
            throw new DomainException(SalesErrors.VersionConflict, $"The proforma changed (version {proforma.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        if (proforma.Status != "OPEN" || proforma.Allocated > 0m)
        {
            throw new DomainException(ProformaErrors.NotVoidable, $"{proforma.ProformaNo} is {proforma.Status} with {Receipting.Money(proforma.Allocated)} allocated: only an OPEN proforma without allocations is voided (E-FIS1b-01-11).");
        }

        if (await SalesSql.ScalarAsync<bool>(
                context,
                """
                SELECT EXISTS (SELECT 1 FROM tax.fiscal_authorization_proforma x JOIN tax.fiscal_authorization a ON a.authorization_id = x.authorization_id
                               WHERE x.proforma_id = @p AND a.status NOT IN ('REJECTED', 'EXPIRED'))
                """,
                cancellationToken, ("p", command.ProformaId)).ConfigureAwait(false))
        {
            throw new DomainException(ProformaErrors.NotVoidable, $"{proforma.ProformaNo} is cited by a fiscal authorization; it is not voided.");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ProformaVoided",
                1,
                Proformas.Aggregate,
                command.ProformaId,
                await SalesSql.NextEventVersionAsync(context, Proformas.Aggregate, command.ProformaId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { proformaId = command.ProformaId, proformaNo = proforma.ProformaNo, reason }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await ProformaInvoicing.SetStatusAsync(context, [proforma], "VOIDED", null, eventId, CommandType, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { proformaId = command.ProformaId, status = "VOIDED", version = proforma.Version + 1 });
    }
}
