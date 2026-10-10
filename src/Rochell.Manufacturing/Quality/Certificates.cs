using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Quality;

// LAB1-03 (baseline §4.6, E-LAB1-03-2…6): the compression certificate of one lot and one break date. It is a snapshot — what it
// certifies is kept when it is issued — with a public code for the QR. It shows tested results only, never the 28-day estimate.

/// <summary>
/// E-LAB1-03-2…4, 6: Calidad issues the certificate of a lot's specimens broken on <paramref name="BreakDate"/> (at least one valid). The
/// lot needs its field code. <paramref name="DeliveryId"/>, when given, is a delivery that took the lot: its customer, site and number
/// print on the certificate (E-LAB1-03-3).
/// </summary>
public sealed record IssueLabCertificate(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId, DateOnly BreakDate, Guid? DeliveryId = null)
    : IPlantScopedCommand;

/// <summary>E-LAB1-03-5: Calidad voids a certificate with a reason; the public verification then says it is void.</summary>
public sealed record VoidLabCertificate(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid CertificateId, string Reason) : IPlantScopedCommand;

internal static class LabCertificates
{
    public const string Aggregate = "LabCertificate";
    public const int Decimals = 2;
    private const string CodeAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int CodeLength = 24;

    /// <summary>E-LAB1-03-3: <c>CR-&lt;field code&gt;-&lt;DDMMYY&gt;</c>; the next ones of the same lot and date add <c>-2</c>, <c>-3</c>…</summary>
    public static string Number(string fieldCode, DateOnly breakDate, int seq)
        => $"CR-{fieldCode}-{breakDate.ToString("ddMMyy", CultureInfo.InvariantCulture)}" + (seq > 1 ? string.Create(CultureInfo.InvariantCulture, $"-{seq}") : string.Empty);

    public static string PublicCode() => RandomNumberGenerator.GetString(CodeAlphabet, CodeLength);

    public static string R(decimal value) => decimal.Round(value, Decimals, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    private static string? R(decimal? value) => value is { } v ? R(v) : null;

    private static string? Plain(decimal? value) => value?.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// E-LAB1-03-5: voiding a specimen voids, by themselves, the ISSUED certificates that show it — with the cause and the specimen's reason.
    /// Returns the numbers voided.
    /// </summary>
    public static async Task<IReadOnlyList<string>> VoidForTestAsync(CommandContext context, Guid testId, string why, string commandType, CancellationToken cancellationToken)
    {
        var certificates = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.certificate_id, c.certificate_no, c.version FROM qa.certificate c JOIN qa.certificate_test x ON x.certificate_id = c.certificate_id
            WHERE c.company_id = @c AND x.test_id = @t AND c.status = 'ISSUED' FOR UPDATE OF c
            """,
            r => (Id: r.GetGuid(0), No: r.GetString(1), Version: r.GetInt32(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", testId)).ConfigureAwait(false);
        var by = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var reason = $"Se anuló una de sus probetas: {why}";
        if (reason.Length > 500)
        {
            reason = reason[..500];
        }

        foreach (var (id, no, version) in certificates)
        {
            await VoidAsync(context, id, no, version, "SPECIMEN_VOIDED", reason, by, commandType, cancellationToken).ConfigureAwait(false);
        }

        return [.. certificates.Select(c => c.No)];
    }

    public static async Task VoidAsync(
        CommandContext context, Guid certificateId, string certificateNo, int version, string cause, string reason, Guid by, string commandType, CancellationToken cancellationToken)
    {
        var eventId = await context.AppendEventAsync(
            new EventDraft("LabCertificateVoided", 1, Aggregate, certificateId, await MfgSql.NextEventVersionAsync(context, Aggregate, certificateId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { certificateId, certificateNo, cause, reason }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE qa.certificate SET status = 'VOIDED', void_cause = @cause, void_reason = @r, voided_by = @by, voided_at = @at, version = @v
            WHERE certificate_id = @id
            """,
            cancellationToken,
            ("cause", cause), ("r", reason), ("by", by), ("at", context.Clock.UtcNow), ("v", version + 1), ("id", certificateId)).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, certificateId, "DOCUMENT", "ISSUED", "VOIDED", commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
    }

    /// <summary>Baseline §4.6: what the certificate shows, kept when it is issued (E-LAB1-03-4/5). Numbers are text with their printed decimals.</summary>
    public static async Task<(JsonObject Snapshot, IReadOnlyList<Guid> Tests)> SnapshotAsync(
        CommandContext context, Lab.LotRow lot, DateOnly breakDate, Guid? deliveryId, CancellationToken cancellationToken)
    {
        var c = context.CompanyId;
        var toMpa = await Lab.ParameterAsync(context.Connection, context.Transaction, c, "KGCM2_TO_MPA", cancellationToken).ConfigureAwait(false);
        var specimens = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT t.test_id, coalesce(t.width_cm, n.nominal_width_cm), coalesce(t.height_cm, n.nominal_height_cm), coalesce(t.length_cm, n.nominal_length_cm), t.nominal_used,
                   t.gross_area_cm2, t.weight_kg, t.load_kg, t.age_days, t.strength_kgcm2, t.block_condition,
                   coalesce((SELECT ft.name FROM qa.failure_type ft WHERE ft.company_id = t.company_id AND ft.code = t.failure_type), (SELECT d.name FROM qa.failure_type_default d WHERE d.code = t.failure_type))
            FROM qa.compression_test t
            JOIN mfg.fg_lot f ON f.lot_id = t.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id
            LEFT JOIN qa.item_spec n ON n.company_id = t.company_id AND n.item_id = r.item_id AND n.version = t.spec_version
            WHERE t.company_id = @c AND t.lot_id = @l AND t.break_date = @d AND t.status = 'RECORDED'
            ORDER BY t.recorded_at, t.line_no
            """,
            r => (Id: r.GetGuid(0), W: r.NullableDecimal(1), H: r.NullableDecimal(2), L: r.NullableDecimal(3), Nominal: r.GetBoolean(4), Area: r.GetDecimal(5), Weight: r.NullableDecimal(6),
                  Load: r.GetDecimal(7), Age: r.GetInt32(8), Strength: r.GetDecimal(9), Condition: r.NullableString(10), Failure: r.NullableString(11)),
            cancellationToken,
            ("c", c), ("l", lot.LotId), ("d", breakDate)).ConfigureAwait(false);
        if (specimens.Count == 0)
        {
            throw new DomainException(QualityErrors.CertificateRefused, $"The lot has no valid specimen broken on {breakDate:yyyy-MM-dd} (E-LAB1-03-4).");
        }

        var stats = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT count(*)::int, avg(t.strength_kgcm2), min(t.strength_kgcm2), max(t.strength_kgcm2), stddev_samp(t.strength_kgcm2),
                   mode() WITHIN GROUP (ORDER BY t.block_condition),
                   (SELECT coalesce((SELECT ft.name FROM qa.failure_type ft WHERE ft.company_id = @c AND ft.code = m.code), (SELECT d.name FROM qa.failure_type_default d WHERE d.code = m.code))
                    FROM (SELECT mode() WITHIN GROUP (ORDER BY x.failure_type) AS code FROM qa.compression_test x
                          WHERE x.company_id = @c AND x.lot_id = @l AND x.break_date = @d AND x.status = 'RECORDED') m)
            FROM qa.compression_test t WHERE t.company_id = @c AND t.lot_id = @l AND t.break_date = @d AND t.status = 'RECORDED'
            """,
            r => (N: r.GetInt32(0), Avg: r.GetDecimal(1), Min: r.GetDecimal(2), Max: r.GetDecimal(3), Std: r.NullableDecimal(4), Condition: r.NullableString(5), Failure: r.NullableString(6)),
            cancellationToken,
            ("c", c), ("l", lot.LotId), ("d", breakDate)).ConfigureAwait(false)).Single();
        var (n, avg, min, max, std, condition, failure) = stats;
        var absorption = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT count(*)::int, avg(absorption_kgm3), avg(density_kgm3) FROM qa.absorption_test WHERE company_id = @c AND lot_id = @l AND status = 'RECORDED' HAVING count(*) > 0",
            r => (Blocks: r.GetInt32(0), Absorption: r.GetDecimal(1), Density: r.GetDecimal(2)),
            cancellationToken,
            ("c", c), ("l", lot.LotId)).ConfigureAwait(false)).FirstOrDefault();
        var origin = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.code, i.description, m.code, m.name, m.short_code, sh.code, p.code, coalesce(p.name, p.code)
            FROM mfg.fg_lot f JOIN mfg.production_run r ON r.run_id = f.run_id JOIN md.item i ON i.item_id = r.item_id JOIN md.machine m ON m.machine_id = r.machine_id
            JOIN mfg.shift sh ON sh.shift_id = r.shift_id JOIN md.plant p ON p.plant_id = r.plant_id
            WHERE f.lot_id = @l
            """,
            r => (ItemCode: r.GetString(0), Item: r.GetString(1), MachineCode: r.GetString(2), Machine: r.GetString(3), Short: r.NullableString(4), Shift: r.GetString(5), PlantCode: r.GetString(6),
                  Plant: r.GetString(7)),
            cancellationToken,
            ("l", lot.LotId)).ConfigureAwait(false)).Single();
        JsonObject? delivery = null;
        if (deliveryId is { } d)
        {
            var rows = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT dv.delivery_no, p.legal_name, p.rnc, o.site_address, o.order_no
                FROM log.delivery dv JOIN sal.sales_order o ON o.sales_order_id = dv.sales_order_id JOIN md.party p ON p.party_id = o.party_id
                WHERE dv.company_id = @c AND dv.delivery_id = @d
                  AND EXISTS (SELECT 1 FROM log.delivery_line_lot x JOIN log.delivery_line dl ON dl.delivery_line_id = x.delivery_line_id WHERE dl.delivery_id = dv.delivery_id AND x.lot_id = @l)
                """,
                r => (No: r.GetString(0), Customer: r.GetString(1), Rnc: r.NullableString(2), Site: r.NullableString(3), Order: r.GetString(4)),
                cancellationToken,
                ("c", c), ("d", d), ("l", lot.LotId)).ConfigureAwait(false);
            var row = rows.Count == 1 ? rows[0] : throw new DomainException(QualityErrors.CertificateRefused, "The delivery does not exist or did not take this lot (E-LAB1-03-3).");
            delivery = new JsonObject { ["deliveryNo"] = row.No, ["orderNo"] = row.Order, ["customer"] = row.Customer, ["customerRnc"] = row.Rnc, ["site"] = row.Site };
        }

        async Task<string?> Text(string code) => await MfgSql.ScalarAsync<string>(context, "SELECT qa.parameter_text(@c, @p)", cancellationToken, ("c", c), ("p", code)).ConfigureAwait(false);
        var issuer = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT legal_name, rnc FROM md.company WHERE company_id = @c", r => (Name: r.GetString(0), Rnc: r.GetString(1)), cancellationToken, ("c", c))
            .ConfigureAwait(false)).Single();
        var issuedBy = await MfgSql.ScalarAsync<string>(
            context, "SELECT coalesce(u.display_name, u.email) FROM iam.user u WHERE u.user_id = @u", cancellationToken, ("u", await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)))
            .ConfigureAwait(false);
        var line = 0;
        var snapshot = new JsonObject
        {
            ["issuer"] = new JsonObject { ["name"] = issuer.Name, ["rnc"] = issuer.Rnc },
            ["lot"] = new JsonObject
            {
                ["fieldCode"] = lot.FieldCode,
                ["lotCode"] = lot.LotCode,
                ["itemCode"] = origin.ItemCode,
                ["item"] = origin.Item,
                ["machineCode"] = origin.MachineCode,
                ["machine"] = origin.Machine,
                ["machineShortCode"] = origin.Short,
                ["shift"] = origin.Shift,
                ["plant"] = origin.Plant,
                ["productionDate"] = lot.ProductionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            ["breakDate"] = breakDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["delivery"] = delivery,
            ["equipment"] = new JsonObject { ["brand"] = await Text("EQUIPMENT_BRAND").ConfigureAwait(false), ["model"] = await Text("EQUIPMENT_MODEL").ConfigureAwait(false), ["serial"] = await Text("EQUIPMENT_SERIAL").ConfigureAwait(false) },
            ["signer"] = new JsonObject { ["name"] = await Text("CERT_SIGNER_NAME").ConfigureAwait(false), ["title"] = await Text("CERT_SIGNER_TITLE").ConfigureAwait(false) },
            ["issuedBy"] = issuedBy,
            ["specimens"] = new JsonArray([.. specimens.Select(s => (JsonNode)new JsonObject
            {
                ["line"] = ++line,
                ["widthCm"] = Plain(s.W),
                ["heightCm"] = Plain(s.H),
                ["lengthCm"] = Plain(s.L),
                ["nominalUsed"] = s.Nominal,
                ["areaCm2"] = R(s.Area),
                ["weightKg"] = Plain(s.Weight),
                ["loadKg"] = Plain(s.Load),
                ["ageDays"] = s.Age,
                ["strengthKgcm2"] = R(s.Strength),
                ["strengthMpa"] = R(s.Strength * toMpa),
                ["condition"] = s.Condition,
                ["failure"] = s.Failure,
            })]),
            ["summary"] = new JsonObject
            {
                ["specimens"] = n,
                ["avgKgcm2"] = R(avg),
                ["avgMpa"] = R(avg * toMpa),
                ["minKgcm2"] = R(min),
                ["minMpa"] = R(min * toMpa),
                ["maxKgcm2"] = R(max),
                ["cvPercent"] = std is { } sd && avg != 0m ? R(sd / avg * 100m) : null,
                ["condition"] = condition,
                ["failure"] = failure,
            },
            ["absorption"] = absorption.Blocks > 0
                ? new JsonObject { ["blocks"] = absorption.Blocks, ["absorptionKgm3"] = R(absorption.Absorption), ["densityKgm3"] = R(absorption.Density) }
                : null,
        };
        return (snapshot, [.. specimens.Select(s => s.Id)]);
    }
}

[RequiresPermission("fg_lot:final_release", StepUp = true)]
public sealed class IssueLabCertificateHandler : ICommandHandler<IssueLabCertificate>
{
    public string CommandType => "Manufacturing.IssueLabCertificate";

    public async Task<string> HandleAsync(IssueLabCertificate command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var lot = await LabTests.LotOfPlantAsync(context, command.PlantId, command.LotId, cancellationToken).ConfigureAwait(false);
        // The lot's lock orders the certificates of one lot and date (their numbers).
        await Lots.Lots.LockAsync(context, command.PlantId, command.LotId, null, cancellationToken).ConfigureAwait(false);
        if (lot.FieldCode is null)
        {
            throw new DomainException(QualityErrors.CertificateRefused, "The lot has no field code yet: the certificate's number is made from it (E-LAB1-03-2).");
        }

        var (snapshot, tests) = await LabCertificates.SnapshotAsync(context, lot, command.BreakDate, command.DeliveryId, cancellationToken).ConfigureAwait(false);
        var seq = (await MfgSql.ScalarAsync<int?>(
            context, "SELECT max(seq) FROM qa.certificate WHERE company_id = @c AND lot_id = @l AND break_date = @d", cancellationToken,
            ("c", context.CompanyId), ("l", lot.LotId), ("d", command.BreakDate)).ConfigureAwait(false) ?? 0) + 1;
        var number = LabCertificates.Number(lot.FieldCode, command.BreakDate, seq);
        snapshot["certificateNo"] = number;
        var id = context.Ids.NewId();
        var code = LabCertificates.PublicCode();
        var issuedAt = context.Clock.UtcNow;
        snapshot["issuedAt"] = issuedAt.ToString("O", CultureInfo.InvariantCulture);
        var eventId = await context.AppendEventAsync(
            new EventDraft("LabCertificateIssued", 1, LabCertificates.Aggregate, id, 1,
                JsonSerializer.Serialize(new { certificateId = id, certificateNo = number, lotId = lot.LotId, breakDate = command.BreakDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), deliveryId = command.DeliveryId, specimens = tests.Count }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO qa.certificate (certificate_id, company_id, lot_id, break_date, seq, certificate_no, delivery_id, public_code, snapshot, status, issued_by, issued_at, version)
            VALUES (@id, @c, @l, @d, @seq, @no, @dv, @code, CAST(@snap AS jsonb), 'ISSUED', @by, @at, 1)
            """,
            cancellationToken,
            ("id", id), ("c", context.CompanyId), ("l", lot.LotId), ("d", command.BreakDate), ("seq", seq), ("no", number), ("dv", command.DeliveryId), ("code", code),
            ("snap", snapshot.ToJsonString()), ("by", await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)), ("at", issuedAt)).ConfigureAwait(false);
        foreach (var test in tests)
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "INSERT INTO qa.certificate_test (certificate_id, company_id, test_id) VALUES (@id, @c, @t)", cancellationToken,
                ("id", id), ("c", context.CompanyId), ("t", test)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(LabCertificates.Aggregate, id, "DOCUMENT", null, "ISSUED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { certificateId = id, certificateNo = number, publicCode = code, status = "ISSUED", specimens = tests.Count });
    }
}

[RequiresPermission("fg_lot:final_release", StepUp = true)]
public sealed class VoidLabCertificateHandler : ICommandHandler<VoidLabCertificate>
{
    public string CommandType => "Manufacturing.VoidLabCertificate";

    public async Task<string> HandleAsync(VoidLabCertificate command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var why = Lots.Lots.Reason(command.Reason);
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.certificate_no, c.status, c.version, r.plant_id FROM qa.certificate c JOIN mfg.fg_lot f ON f.lot_id = c.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id
            WHERE c.company_id = @c AND c.certificate_id = @id FOR UPDATE OF c
            """,
            r => (No: r.GetString(0), Status: r.GetString(1), Version: r.GetInt32(2), Plant: r.GetGuid(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", command.CertificateId)).ConfigureAwait(false);
        var row = rows.Count == 1 ? rows[0] : throw new DomainException(ManufacturingErrors.NotFound, "The certificate does not exist.");
        if (row.Plant != command.PlantId)
        {
            throw new DomainException(ManufacturingErrors.PlantMismatch, "The certificate's lot belongs to another plant.");
        }

        if (row.Status != "ISSUED")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, "The certificate is already voided.");
        }

        await LabCertificates.VoidAsync(
            context, command.CertificateId, row.No, row.Version, "MANUAL", why, await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false), CommandType, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { certificateId = command.CertificateId, certificateNo = row.No, status = "VOIDED" });
    }
}
