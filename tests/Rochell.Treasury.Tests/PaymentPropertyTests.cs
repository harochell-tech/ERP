using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Reconciliation;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Statements;
using Xunit;
using Xunit.Abstractions;

namespace Rochell.Treasury.Tests;

/// <summary>
/// INV-P (E-VS2-09-1/2/5): random but reproducible (seeded) sequences of prepare, update, void, release, reverse, statement import,
/// match, unmatch and bank charges, against a simulated bank that executes transfers, returns reversed ones, charges fees and hands
/// out cumulative statements of the day, as real banks do. After EVERY step the invariants are re-summed by SQL; every 25 steps
/// and at the end AP-GL, PAY-APPL, ACC-EVIDENCE and BANK-GL must have no ERROR. A failure reports the seed and the steps so far.
/// ROCHELL_INVP_SEEDS (default 1,2,3) and ROCHELL_INVP_STEPS (default 150) tune the run (workflow `inv-p` for long runs).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PaymentPropertyTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private const string R10 = "0192f001-0000-7000-8000-000000000010";

    /// <summary>E-VS2-09-2: reconciliations checked every this many steps (and at the end).</summary>
    private const int CheckpointEvery = 25;

    /// <summary>Rejections a random sequence legitimately runs into (it acts on stale choices); anything else fails the run.</summary>
    private static readonly HashSet<string> ExpectedRejections = new(StringComparer.Ordinal)
    {
        PaymentErrors.ApplicationExceedsOpenAmount, PaymentErrors.NotPrepared, PaymentErrors.NotReversible,
        StatementErrors.PaymentNotReleased, StatementErrors.PaymentNotReversed, StatementErrors.ReturnWithoutTransfer, StatementErrors.ReturnAlreadyMatched,
        StatementErrors.LineNotUnmatched, StatementErrors.LineNotMatched, StatementErrors.PaymentReversed,
    };

    private static readonly (string Name, string Sql)[] Invariants =
    [
        ("journals balanced", "SELECT count(*) FROM (SELECT journal_id FROM fin.gl_entry GROUP BY journal_id HAVING sum(debit) <> sum(credit)) x"),
        ("live applications = payment amount (REVERSED 0)", """
            SELECT count(*) FROM fin.payment p
            WHERE p.status::text IN ('RELEASED', 'CLEARED', 'REVERSED')
              AND (SELECT coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0) FROM fin.ap_application a WHERE a.payment_id = p.payment_id)
                  <> CASE WHEN p.status::text = 'REVERSED' THEN 0 ELSE p.amount END
            """),
        ("open ≥ 0 and original − open = live applications", """
            SELECT count(*) FROM fin.ap_document d
            WHERE d.open_amount < 0 OR d.original_amount - d.open_amount <>
              (SELECT coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0) FROM fin.ap_application a WHERE a.ap_doc_id = d.ap_doc_id)
            """),
        ("CLEARED ⇔ one matched DEBIT line; RELEASED ⇒ none", """
            SELECT count(*) FROM fin.payment p
            WHERE (p.status::text = 'CLEARED' AND (SELECT count(*) FROM fin.bank_statement_line l WHERE l.matched_payment_id = p.payment_id AND l.direction = 'DEBIT') <> 1)
               OR (p.status::text = 'RELEASED' AND EXISTS (SELECT 1 FROM fin.bank_statement_line l WHERE l.matched_payment_id = p.payment_id))
            """),
        ("AP subledger = AP GL", """
            SELECT count(*) FROM (SELECT (SELECT coalesce(sum(open_amount), 0) FROM fin.ap_document) AS subledger,
                                         (SELECT coalesce(sum(credit - debit), 0) FROM fin.gl_entry WHERE account_role = 'AP_CONTROL') AS gl) x
            WHERE subledger <> gl
            """),
    ];

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        foreach (var seed in (Environment.GetEnvironmentVariable("ROCHELL_INVP_SEEDS") ?? "1,2,3").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            data.Add(int.Parse(seed, CultureInfo.InvariantCulture));
        }

        return data;
    }

    /// <summary>A movement of the simulated bank, in the order the bank records it.</summary>
    private sealed record BankLine(string Kind, Guid? PaymentId, string Description, bool Debit, decimal Amount);

    [Trait("AcceptanceVs2", "INV-P")]
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Random_payment_and_bank_sequences_keep_every_invariant(int seed)
    {
        var steps = int.Parse(Environment.GetEnvironmentVariable("ROCHELL_INVP_STEPS") ?? "150", CultureInfo.InvariantCulture);
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(invoices: 2, bankCode: "TEST_BANK");
        await h.CreateActiveMapAsync("BANK_CHARGES", await h.CreateAccountAsync("6105", "Cargos bancarios", isControl: false));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{R10}' AND version = 1");
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

        var random = new Random(seed);
        var bank = new List<BankLine>();
        var executed = new HashSet<Guid>();
        var returned = new HashSet<Guid>();
        var importedLines = 0;
        var log = new StringBuilder();
        var counter = 0;
        string Key() => $"k-{++counter}";
        decimal Cents(int maxCents) => random.Next(1, maxCents + 1) / 100m;
        string M(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

        async Task Step(string description, Func<Task> run)
        {
            log.AppendLine(CultureInfo.InvariantCulture, $"{counter}: {description}");
            try
            {
                await run();
            }
            catch (DomainException ex) when (ExpectedRejections.Contains(ex.Code))
            {
                log.AppendLine(CultureInfo.InvariantCulture, $"   rejected {ex.Code}");
            }
        }

        async Task<List<(Guid Id, string No, decimal Amount, long Version)>> Payments(string status)
        {
            var list = new List<(Guid, string, decimal, long)>();
            await using var command = h.Admin.CreateCommand("SELECT payment_id, payment_no, amount, version FROM fin.payment WHERE status::text = @s ORDER BY payment_no");
            command.Parameters.AddWithValue("s", status);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add((reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetInt64(3)));
            }

            return list;
        }

        async Task<List<(Guid Id, long Version, string Direction, string Description, string Status, Guid? Payment)>> Lines()
        {
            var list = new List<(Guid, long, string, string, string, Guid?)>();
            await using var command = h.Admin.CreateCommand("SELECT line_id, version, direction, description, status, matched_payment_id FROM fin.bank_statement_line ORDER BY value_date, line_id");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add((reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetGuid(5)));
            }

            return list;
        }

        T Pick<T>(List<T> items) => items[random.Next(items.Count)];

        // The bank hands out the day's cumulative statement (opening 0.00, every movement so far): lines already imported come
        // back as IDM-04 duplicates, only new ones are inserted.
        async Task ImportAsync(bool force)
        {
            if (!force && bank.Count == importedLines)
            {
                return;
            }

            var csv = new StringBuilder("Fecha,Referencia,Descripcion,Debito,Credito\n");
            foreach (var line in bank)
            {
                csv.Append(CultureInfo.InvariantCulture, $"{today:dd/MM/yyyy},,{line.Description},{(line.Debit ? M(line.Amount) : "")},{(line.Debit ? "" : M(line.Amount))}\n");
            }

            var closing = bank.Sum(l => l.Debit ? -l.Amount : l.Amount);
            await Step($"import {bank.Count} bank lines", async () =>
            {
                try
                {
                    await h.RunAsync(
                        new ImportBankStatement(h.CompanyId, p.Treasurer, Key(), p.BankAccount, $"dia-{counter}.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv.ToString())), today, today, 0m, closing),
                        new ImportBankStatementHandler());
                }
                catch (DomainException ex) when (ex.Code == StatementErrors.AlreadyImported)
                {
                    log.AppendLine("   same file as before");
                }

                importedLines = bank.Count;
            });
        }

        async Task CheckpointAsync(string when)
        {
            await ImportAsync(force: await h.ScalarAsync<long>("SELECT count(*) FROM fin.bank_statement") == 0);
            var run = await h.RunAsync(new RunReconciliation(h.CompanyId, p.Controller, Key(), ["AP-GL", "PAY-APPL", "ACC-EVIDENCE", "BANK-GL"], today), new RunReconciliationHandler());
            var errors = JsonDocument.Parse(run.ResultPayload).RootElement.GetProperty("runs").EnumerateArray()
                .Where(x => x.GetProperty("errors").GetInt32() > 0).Select(x => x.GetProperty("code").GetString()).ToList();
            var detail = errors.Count == 0 ? string.Empty : await h.ScalarAsync<string>(
                "SELECT string_agg(x.classification || ' ' || x.match_key || ' ' || coalesce(x.value_a::text, '') || ' / ' || coalesce(x.value_b::text, ''), '; ') FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id) WHERE x.severity = 'ERROR' AND r.command_id = (SELECT command_id FROM core.command_log WHERE result_ref = @r)",
                ("r", run.ResultRef));
            Assert.True(errors.Count == 0, $"Seed {seed}, {when}: reconciliations with ERROR findings: {string.Join(", ", errors)} ({detail})\n{log}");
        }

        for (var step = 0; step < steps; step++)
        {
            switch (random.Next(12))
            {
                case 0 or 1:
                    {
                        var doc = await h.ScalarAsync<Guid?>("SELECT ap_doc_id FROM fin.ap_document WHERE open_amount > 0 ORDER BY random() LIMIT 1");
                        if (doc is { } apDoc)
                        {
                            var open = await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", apDoc));
                            var amount = Math.Min(open, Cents(120_000));
                            await Step($"prepare {M(amount)} on {apDoc}", () => h.RunAsync(
                                new PrepareSupplierPayment(h.CompanyId, p.Treasurer, Key(), p.Supplier, p.BankAccount, p.PartyBankAccount, today, null, [new(apDoc, amount)]),
                                new PrepareSupplierPaymentHandler()));
                        }

                        break;
                    }

                case 2:
                    {
                        var prepared = await Payments("PREPARED");
                        if (prepared.Count > 0)
                        {
                            var (id, no, amount, version) = Pick(prepared);
                            var apDoc = await h.ScalarAsync<Guid>("SELECT ap_doc_id FROM fin.payment_allocation WHERE payment_id = @p AND payment_version = @v LIMIT 1", ("p", id), ("v", version));
                            var newAmount = Cents(120_000);
                            await Step($"update {no} to {M(newAmount)}", () => h.RunAsync(
                                new UpdatePreparedPayment(h.CompanyId, p.Treasurer, Key(), id, version, p.BankAccount, p.PartyBankAccount, today, null, [new(apDoc, newAmount)]),
                                new UpdatePreparedPaymentHandler()));
                        }

                        break;
                    }

                case 3:
                    {
                        var prepared = await Payments("PREPARED");
                        if (prepared.Count > 0)
                        {
                            var (id, no, _, version) = Pick(prepared);
                            await Step($"void {no}", () => h.RunAsync(new VoidPayment(h.CompanyId, p.Treasurer, Key(), id, version, "Anulado en la prueba"), new VoidPaymentHandler()));
                        }

                        break;
                    }

                case 4 or 5:
                    {
                        var prepared = await Payments("PREPARED");
                        if (prepared.Count > 0)
                        {
                            var (id, no, _, version) = Pick(prepared);
                            await Step($"release {no}", () => h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, Key(), id, version), new ReleaseSupplierPaymentHandler()));
                        }

                        break;
                    }

                case 6:
                    {
                        var live = (await Payments("RELEASED")).Concat(await Payments("CLEARED")).ToList();
                        if (live.Count > 0 && random.Next(2) == 0)
                        {
                            var (id, no, _, version) = Pick(live);
                            await Step($"reverse {no}", () => h.RunAsync(new ReversePayment(h.CompanyId, p.Controller, Key(), id, version, "Revertido por la prueba"), new ReversePaymentHandler()));
                        }

                        break;
                    }

                case 7:
                    {
                        // The bank executes a released transfer (once), or returns a reversed one it had executed (once), or charges a fee.
                        var released = (await Payments("RELEASED")).Where(x => !executed.Contains(x.Id)).ToList();
                        var toReturn = (await Payments("REVERSED")).Where(x => executed.Contains(x.Id) && !returned.Contains(x.Id)).ToList();
                        if (released.Count > 0 && random.Next(3) != 0)
                        {
                            var (id, no, amount, _) = Pick(released);
                            executed.Add(id);
                            bank.Add(new BankLine("TRANSFER", id, $"Transferencia {no}", true, amount));
                            log.AppendLine(CultureInfo.InvariantCulture, $"{counter}: bank executes {no}");
                        }
                        else if (toReturn.Count > 0 && random.Next(2) == 0)
                        {
                            var (id, no, amount, _) = Pick(toReturn);
                            returned.Add(id);
                            bank.Add(new BankLine("RETURN", id, $"Devolucion {no}", false, amount));
                            log.AppendLine(CultureInfo.InvariantCulture, $"{counter}: bank returns {no}");
                        }
                        else
                        {
                            bank.Add(new BankLine("CHARGE", null, "Comision", true, Cents(5_000)));
                            log.AppendLine(CultureInfo.InvariantCulture, $"{counter}: bank charges a fee");
                        }

                        break;
                    }

                case 8:
                    await ImportAsync(force: false);
                    break;

                case 9 or 10:
                    {
                        // The treasurer matches a transfer to its payment, or a return to its reversed payment.
                        var lines = (await Lines()).Where(l => l.Status == "UNMATCHED" && !l.Description.StartsWith("Comision", StringComparison.Ordinal)).ToList();
                        if (lines.Count > 0)
                        {
                            var line = Pick(lines);
                            var no = line.Description[(line.Description.IndexOf(' ', StringComparison.Ordinal) + 1)..];
                            var payment = await h.ScalarAsync<Guid>("SELECT payment_id FROM fin.payment WHERE payment_no = @n", ("n", no));
                            var version = await h.ScalarAsync<long>("SELECT version FROM fin.payment WHERE payment_id = @p", ("p", payment));
                            await Step($"match {line.Description}", () => h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, Key(), line.Id, line.Version, payment, version), new MatchBankLineHandler()));
                        }

                        break;
                    }

                default:
                    {
                        // The Controller unmatches a line (sometimes) or recognizes an unmatched fee.
                        var lines = await Lines();
                        var fees = lines.Where(l => l.Status == "UNMATCHED" && l.Description.StartsWith("Comision", StringComparison.Ordinal)).ToList();
                        var matched = lines.Where(l => l.Status == "MATCHED").ToList();
                        if (matched.Count > 0 && random.Next(4) == 0)
                        {
                            var line = Pick(matched);
                            await Step($"unmatch {line.Description}", () => h.RunAsync(new UnmatchBankLine(h.CompanyId, p.Controller, Key(), line.Id, line.Version, "Conciliada por error"), new UnmatchBankLineHandler()));
                        }
                        else if (fees.Count > 0)
                        {
                            var line = Pick(fees);
                            await Step($"recognize fee {line.Id}", () => h.RunAsync(new RecognizeBankCharge(h.CompanyId, p.Controller, Key(), line.Id, line.Version), new RecognizeBankChargeHandler()));
                        }

                        break;
                    }
            }

            foreach (var (name, sql) in Invariants)
            {
                var violations = await h.ScalarAsync<long>(sql);
                Assert.True(violations == 0, $"Seed {seed}, after step {step}: invariant '{name}' has {violations} violation(s).\n{log}");
            }

            if ((step + 1) % CheckpointEvery == 0)
            {
                await CheckpointAsync($"checkpoint after step {step}");
            }
        }

        await CheckpointAsync("end");
        output.WriteLine($"seed {seed}: {counter} commands, {bank.Count} bank movements, {executed.Count} transfers executed, {returned.Count} returned");
    }
}
