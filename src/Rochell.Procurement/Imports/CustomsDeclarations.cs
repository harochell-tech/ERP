using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.Imports;

/// <summary>
/// E-USD1-04-1/2: Cuentas por pagar registers the DUA and it is posted at once (P-39) — duties and other charges to «Importaciones por
/// liquidar», its ITBIS recoverable, the total owed to the DGA (<paramref name="PartyId"/>, a local supplier) as a payable paid like any
/// other. <paramref name="CifAmount"/> is informative (the 606 / IT-1 come in USD1-06).
/// </summary>
public sealed record RegisterCustomsDeclaration(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PartyId,
    Guid PlantId,
    string DuaNo,
    DateOnly DuaDate,
    DateOnly DueDate,
    decimal CifAmount,
    decimal DutiesAmount,
    decimal ItbisAmount,
    decimal OtherAmount) : ICommand;

/// <summary>E-USD1-04-1/7: the exact reversal of a DUA while it is unpaid and in no live settlement.</summary>
public sealed record ReverseCustomsDeclaration(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DuaId, long ExpectedVersion, string Reason) : ICommand;

public static class ImportErrors
{
    public const string DuaInvalid = "DUA_INVALID";
    public const string DuaNumberUsed = "DUA_NUMBER_USED";
    public const string DuaPartyInvalid = "DUA_PARTY_INVALID";
    public const string NotFound = "IMPORT_DOCUMENT_NOT_FOUND";
    public const string InvalidState = "IMPORT_INVALID_STATE";
    public const string VersionConflict = "IMPORT_VERSION_CONFLICT";
    public const string ApNotOpen = "IMPORT_AP_NOT_OPEN";

    /// <summary>E-USD1-04-7: a document in a DRAFT or POSTED settlement is neither reversed nor settled again.</summary>
    public const string InSettlement = "IMPORT_DOCUMENT_IN_SETTLEMENT";

    /// <summary>E-USD1-04-3: what a settlement may gather.</summary>
    public const string SettlementDocumentInvalid = "SETTLEMENT_DOCUMENT_INVALID";
    public const string SettlementInvalid = "SETTLEMENT_INVALID";
}

internal static class CustomsDeclarations
{
    public const string Aggregate = "CustomsDeclaration";
    public const string P39 = "P-39";

    public static string Text(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    public static void RequireAmount(decimal value, string name, bool positive = false)
    {
        if (value < 0m || (positive && value == 0m) || decimal.Round(value, 2) != value)
        {
            throw new DomainException(ImportErrors.DuaInvalid, $"The {name} is {(positive ? "greater than zero" : "zero or more")}, with at most 2 decimals.");
        }
    }
}

[RequiresPermission("supplier_invoice:post")]
public sealed class RegisterCustomsDeclarationHandler : ICommandHandler<RegisterCustomsDeclaration>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Procurement.RegisterCustomsDeclaration";

    public async Task<string> HandleAsync(RegisterCustomsDeclaration command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var number = (command.DuaNo ?? string.Empty).Trim().ToUpperInvariant();
        if (number.Length is 0 or > 40)
        {
            throw new DomainException(ImportErrors.DuaInvalid, "The DUA number has 1 to 40 characters.");
        }

        CustomsDeclarations.RequireAmount(command.CifAmount, "CIF value", positive: true);
        CustomsDeclarations.RequireAmount(command.DutiesAmount, "duties");
        CustomsDeclarations.RequireAmount(command.ItbisAmount, "ITBIS");
        CustomsDeclarations.RequireAmount(command.OtherAmount, "other charges");
        var importCost = command.DutiesAmount + command.OtherAmount;
        var payable = importCost + command.ItbisAmount;
        if (payable <= 0m)
        {
            throw new DomainException(ImportErrors.DuaInvalid, "A DUA owes duties, ITBIS or other charges.");
        }

        if (command.DuaDate > BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow) || command.DueDate < command.DuaDate)
        {
            throw new DomainException(ImportErrors.DuaInvalid, "The DUA date cannot be in the future and its due date cannot precede it.");
        }

        var checks = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT coalesce((SELECT status = 'ACTIVE' AND is_supplier AND party_kind = 'LOCAL' FROM md.party WHERE company_id = @c AND party_id = @p), false),
                   EXISTS (SELECT 1 FROM md.plant WHERE company_id = @c AND plant_id = @plant),
                   EXISTS (SELECT 1 FROM pur.customs_declaration WHERE company_id = @c AND dua_no = @no AND status <> 'CANCELLED' AND status <> 'REVERSED')
            """,
            r => (Party: r.GetBoolean(0), Plant: r.GetBoolean(1), Used: r.GetBoolean(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", command.PartyId),
            ("plant", command.PlantId),
            ("no", number)).ConfigureAwait(false)).Single();
        if (!checks.Party)
        {
            throw new DomainException(ImportErrors.DuaPartyInvalid, "The DUA is owed to the DGA: an ACTIVE local supplier (E-USD1-04-1).");
        }

        if (!checks.Plant)
        {
            throw new DomainException(ProcurementErrors.PlantMismatch, "The plant does not exist.");
        }

        if (checks.Used)
        {
            throw new DomainException(ImportErrors.DuaNumberUsed, $"DUA {number} is already registered.");
        }

        var duaId = context.ResultRef;
        var apDocId = context.Ids.NewId();
        var occurredAt = context.Clock.UtcNow;
        var inputs = new Dictionary<string, string> { ["dua"] = number };
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                CustomsDeclarations.P39,
                command.DuaDate,
                occurredAt,
                [
                    new PostingLineInput("P39-DR-CLR", "import_cost", importCost, PlantId: command.PlantId, PartyId: command.PartyId, SubledgerRef: duaId, Inputs: inputs),
                    new PostingLineInput("P39-DR-ITBIS", "itbis", command.ItbisAmount, PlantId: command.PlantId, Inputs: inputs),
                    new PostingLineInput("P39-CR-AP", "payable", payable, PartyId: command.PartyId, SubledgerRef: apDocId, Inputs: inputs),
                ]),
            cancellationToken).ConfigureAwait(false);
        var creator = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomsDeclarationPosted",
                1,
                CustomsDeclarations.Aggregate,
                duaId,
                1,
                JsonSerializer.Serialize(new
                {
                    duaId,
                    apDocId,
                    duaNo = number,
                    partyId = command.PartyId,
                    plantId = command.PlantId,
                    duaDate = command.DuaDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    cif = CustomsDeclarations.Text(command.CifAmount),
                    duties = CustomsDeclarations.Text(command.DutiesAmount),
                    itbis = CustomsDeclarations.Text(command.ItbisAmount),
                    other = CustomsDeclarations.Text(command.OtherAmount),
                    payable = CustomsDeclarations.Text(payable),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: command.DuaDate),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.customs_declaration (dua_id, company_id, dua_no, dua_date, party_id, cif_amount, duties_amount, itbis_amount, other_amount, due_date,
                  status, accounting_status, posting_event_id, created_by, version, plant_id)
                VALUES (@id, @c, @no, @date, @party, @cif, @duties, @itbis, @other, @due, 'POSTED', 'POSTED', @event, @by, 1, @plant)
                """,
                cancellationToken,
                ("id", duaId),
                ("c", context.CompanyId),
                ("no", number),
                ("date", command.DuaDate),
                ("party", command.PartyId),
                ("cif", command.CifAmount),
                ("duties", command.DutiesAmount),
                ("itbis", command.ItbisAmount),
                ("other", command.OtherAmount),
                ("due", command.DueDate),
                ("event", eventId),
                ("by", creator),
                ("plant", command.PlantId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(ImportErrors.DuaNumberUsed, $"DUA {number} is already registered.");
        }

        await context.AppendStateAsync(CustomsDeclarations.Aggregate, duaId, "DOCUMENT", null, "POSTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.ap_document (ap_doc_id, company_id, party_id, doc_type, source_doc_id, doc_date, due_date, original_amount, open_amount, version)
            VALUES (@id, @c, @party, 'CUSTOMS_DECLARATION', @dua, @date, @due, @amount, @amount, 1)
            """,
            cancellationToken,
            ("id", apDocId),
            ("c", context.CompanyId),
            ("party", command.PartyId),
            ("dua", duaId),
            ("date", command.DuaDate),
            ("due", command.DueDate),
            ("amount", payable)).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            duaId,
            duaNo = number,
            status = "POSTED",
            apDocumentId = apDocId,
            importCost = CustomsDeclarations.Text(importCost),
            payable = CustomsDeclarations.Text(payable),
            journals = new[] { journal.JournalId },
            version = 1,
        });
    }
}

[RequiresPermission("supplier_invoice:reverse", StepUp = true)]
public sealed class ReverseCustomsDeclarationHandler : ICommandHandler<ReverseCustomsDeclaration>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Procurement.ReverseCustomsDeclaration";

    public async Task<string> HandleAsync(ReverseCustomsDeclaration command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var dua = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT status, version, posting_event_id FROM pur.customs_declaration WHERE company_id = @c AND dua_id = @d FOR UPDATE",
            r => (Status: r.GetString(0), Version: r.GetInt64(1), Event: r.GetGuid(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", command.DuaId)).ConfigureAwait(false)).SingleOrDefault();
        if (dua == default)
        {
            throw new DomainException(ImportErrors.NotFound, "The DUA does not exist.");
        }

        if (dua.Version != command.ExpectedVersion)
        {
            throw new DomainException(ImportErrors.VersionConflict, $"The DUA is at version {dua.Version}, not {command.ExpectedVersion}.");
        }

        if (dua.Status != "POSTED")
        {
            throw new DomainException(ImportErrors.InvalidState, $"The DUA is {dua.Status}.");
        }

        await ImportSettlements.RequireNotSettledAsync(context, ImportSettlements.DuaKind, command.DuaId, cancellationToken).ConfigureAwait(false);
        var (apDocId, open) = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT ap_doc_id, open_amount = original_amount FROM fin.ap_document WHERE company_id = @c AND doc_type = 'CUSTOMS_DECLARATION' AND source_doc_id = @d FOR UPDATE",
            r => (r.GetGuid(0), r.GetBoolean(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", command.DuaId)).ConfigureAwait(false)).Single();
        if (!open)
        {
            throw new DomainException(ImportErrors.ApNotOpen, "The DGA has been paid for this DUA; it can no longer be reversed.");
        }

        var journalId = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT journal_id FROM fin.gl_journal WHERE company_id = @c AND source_event_id = @e AND journal_type = 'AUTO'",
            r => r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("e", dua.Event)).ConfigureAwait(false)).Single();
        var occurredAt = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var plan = await _engine.PrepareReversalAsync(context, journalId, businessDate, cancellationToken).ConfigureAwait(false);
        var version = dua.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CustomsDeclarationReversed", 1, CustomsDeclarations.Aggregate, command.DuaId, version,
                JsonSerializer.Serialize(new { duaId = command.DuaId, apDocId, reversedJournalId = journalId, reason }), Publish: true, OccurredAt: occurredAt, BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE pur.customs_declaration SET status = 'REVERSED', accounting_status = 'REVERSED', version = @v WHERE dua_id = @d",
            cancellationToken, ("v", version), ("d", command.DuaId)).ConfigureAwait(false);
        await context.AppendStateAsync(CustomsDeclarations.Aggregate, command.DuaId, "DOCUMENT", "POSTED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.ap_document SET open_amount = 0, version = version + 1 WHERE ap_doc_id = @a", cancellationToken, ("a", apDocId))
            .ConfigureAwait(false);
        var reversal = await _engine.WriteReversalAsync(context, plan, eventId, occurredAt, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { duaId = command.DuaId, status = "REVERSED", journals = new[] { reversal.JournalId }, version });
    }
}
