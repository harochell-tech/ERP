using Npgsql;
using Rochell.Migrations.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Migrations.Tests;

/// <summary>PR-01 schema (btree_gist + md.company) must match the frozen baseline; the table inventory grows only with listed PRs.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SchemaTests(PostgresFixture postgres)
{
    private async Task<string> MigratedDatabaseAsync()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        await Db.Runner(cs).MigrateAsync(TestPaths.MainSource);
        return cs;
    }

    [Fact]
    public async Task Server_is_postgresql_17()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();

        var versionNum = int.Parse(await Db.ScalarAsync<string>(cs, "SHOW server_version_num") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

        Assert.InRange(versionNum, 170000, 179999);
    }

    [Fact]
    public async Task Btree_gist_extension_is_installed()
    {
        var cs = await MigratedDatabaseAsync();

        Assert.NotNull(await Db.ScalarAsync<string>(cs, "SELECT extversion FROM pg_extension WHERE extname = 'btree_gist'"));
    }

    [Fact]
    public async Task Company_columns_match_frozen_shape()
    {
        var cs = await MigratedDatabaseAsync();

        var columns = await Db.ScalarAsync<string>(cs, """
            SELECT string_agg(column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY ordinal_position)
            FROM information_schema.columns WHERE table_schema = 'md' AND table_name = 'company'
            """);

        Assert.Equal("company_id:uuid:NO,rnc:text:NO,legal_name:text:NO", columns);
    }

    [Fact]
    public async Task Company_primary_key_and_rnc_unique_exist()
    {
        var cs = await MigratedDatabaseAsync();

        var constraints = await Db.ScalarAsync<string>(cs, """
            SELECT string_agg(conname || ':' || contype::text, ',' ORDER BY conname)
            FROM pg_constraint WHERE conrelid = 'md.company'::regclass AND contype IN ('p', 'u')
            """);

        Assert.Equal("company_pk:p,company_rnc_uq:u", constraints);
    }

    [Fact]
    public async Task Duplicate_rnc_is_rejected()
    {
        var cs = await MigratedDatabaseAsync();
        await Db.ExecuteAsync(cs, "INSERT INTO md.company VALUES ('0192a000-0000-7000-8000-000000000001', '101000001', 'Empresa A')");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            Db.ExecuteAsync(cs, "INSERT INTO md.company VALUES ('0192a000-0000-7000-8000-000000000002', '101000001', 'Empresa B')"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Theory]
    [InlineData("INSERT INTO md.company VALUES ('0192a000-0000-7000-8000-000000000003', NULL, 'Sin RNC')")]
    [InlineData("INSERT INTO md.company VALUES ('0192a000-0000-7000-8000-000000000004', '101000004', NULL)")]
    [InlineData("INSERT INTO md.company (rnc, legal_name) VALUES ('101000005', 'Sin id')")]
    public async Task Required_columns_are_enforced(string sql)
    {
        var cs = await MigratedDatabaseAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(cs, sql));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, ex.SqlState);
    }

    [Fact]
    public async Task No_tables_outside_authorized_prs_exist()
    {
        var cs = await MigratedDatabaseAsync();

        var tables = await Db.ScalarAsync<string>(cs, """
            SELECT string_agg(table_schema || '.' || table_name, ',' ORDER BY table_schema, table_name)
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'migrations')
            """);

        // PR-01: md.company. PR-02: core.*, obs.request_log. PR-03: iam.*. PR-04: md masters. PR-05: fin.*. PR-06: acc.*. PR-07: inv.*. PR-08: pur.purchase_order*. PR-09: pur.goods_receipt*. PR-10: pur.goods_receipt_reversal. PR-11: pur.receipt_correction. PR-12: tax.*. PR-13a: pur.supplier_invoice*, pur.match_result, fin.ap_document. PR-15: audit.*. PR-16: rec.*, fin.close_snapshot, fin.reopen_request. VS2-01: fin.bank_account, md.party_bank_account, fin.payment, fin.ap_application, fin.bank_statement*. VS2-03: fin.payment_allocation. VS2-05: fin.bank_statement_file, fin.bank_statement_format. FIN1-01: fin.manual_journal*, fin.report_*. VS3-01: log.vehicle, log.driver, md.standard_cost_version, sal.customer_terms_version, sal.price_list_*. VS3-02b: mig.*. VS3-03: sal.sales_order*, sal.credit_check. VS3-04: log.delivery*, log.pod, inv.control_assessment. VS3-05: fin.ar_document, sal.invoice*, tax.external_fiscal_record. VS3-06: sal.credit_note*. VS3-07: fin.receipt*, fin.ar_application, fin.customer_withholding. RNC: md.rnc_registry*. MFG1-01: md.machine, md.standard_cost_material, mfg.shift, mfg.recipe_*. MFG1-03: mfg.production_run, mfg.shift_summary, mfg.material_consumption, mfg.consumption_issue, mfg.fg_lot, mfg.rack, mfg.cost_collector. MFG1-04: mfg.lot_scrap. FIS1-01: tax.fiscal_authorization*. QUO1-01: sal.quote*. UX3-01: rec.recon_classification.
        Assert.Equal(
            "acc.accounting_policy,acc.accounting_policy_parameter,acc.accounting_policy_version,acc.policy_parameter_definition,"
            + "audit.integrity_state,audit.ledger_digest,audit.ledger_seal,"
            + "core.command_log,core.deployment_environment,core.document_link,core.domain_event,core.inbox,core.mail_attempt,core.mail_message,core.outbox,core.state_history,"
            + "fin.account,fin.account_role,fin.account_role_map,fin.ap_application,fin.ap_document,fin.ar_application,fin.ar_document,fin.bank_account,fin.bank_statement,fin.bank_statement_file,fin.bank_statement_format,fin.bank_statement_line,fin.close_component_state,fin.close_snapshot,fin.customer_refund,fin.customer_withholding,fin.exchange_rate,fin.gl_entry,fin.gl_journal,fin.gl_period_balance,fin.manual_journal,fin.manual_journal_line,fin.order_allocation,"
            + "fin.payment,fin.payment_allocation,fin.period,fin.posting_rule,fin.posting_rule_version,fin.proforma_allocation,fin.receipt,fin.receipt_deposit,fin.reopen_request,fin.report_line,fin.report_line_account,fin.report_structure_version,"
            + "iam.permission,iam.role,iam.role_assignment,iam.role_assignment_request,iam.role_permission,iam.session,iam.sod_rule,iam.user,"
            + "inv.control_assessment,inv.inv_quantity_entry,inv.inv_stock_balance,inv.inv_valuation_balance,inv.inv_value_entry,inv.lot,log.delivery,log.delivery_line,log.delivery_line_lot,log.delivery_term_policy,log.driver,log.pod,log.vehicle,"
            + "md.company,md.item,md.location,md.machine,md.party,md.party_bank_account,md.party_email,md.plant,md.rnc_registry,md.rnc_registry_import,md.standard_cost_material,md.standard_cost_version,md.uom,md.uom_conversion,md.valuation_area,mfg.consumption_issue,mfg.cost_collector,mfg.fg_lot,mfg.lot_scrap,mfg.material_consumption,mfg.production_run,mfg.rack,mfg.recipe_line,mfg.recipe_version,mfg.shift,mfg.shift_summary,mig.migration_batch,mig.opening_inventory_line,obs.request_log,"
            + "pur.customs_declaration,pur.expense_category,pur.goods_receipt,pur.goods_receipt_line,pur.goods_receipt_reversal,pur.import_settlement,pur.import_settlement_allocation,pur.import_settlement_document,pur.match_result,pur.purchase_order,pur.purchase_order_line,pur.receipt_correction,pur.supplier_invoice,pur.supplier_invoice_line,"
            + "rec.recon_blocking,rec.recon_classification,rec.recon_definition,rec.recon_exception,rec.recon_run,sal.credit_check,sal.credit_note,sal.credit_note_line,sal.customer_terms_version,sal.delivery_zone,sal.invoice,sal.invoice_line,sal.price_list,sal.price_list_freight,sal.price_list_line,sal.price_list_version,sal.proforma,sal.proforma_line,sal.quote,sal.quote_line,sal.sales_order,sal.sales_order_line,"
            + "tax.external_fiscal_record,tax.fiscal_authorization,tax.fiscal_authorization_consumption,tax.fiscal_authorization_document,tax.fiscal_authorization_line,tax.fiscal_authorization_proforma,tax.fiscal_rule,tax.fiscal_rule_source,tax.fiscal_rule_test_run,tax.fiscal_rule_version,tax.fiscal_rule_version_source,tax.tax_determination,tax.tax_determination_line",
            tables);
    }

    /// <summary>E-VS2-01-6: migration 0023 opens BANK-REC in every period that already exists.</summary>
    [Fact]
    public async Task Bank_reconciliation_component_is_opened_in_existing_periods()
    {
        var cs = await postgres.CreateEmptyDatabaseAsync();
        using var scratch = new ScratchMigrations();
        // Migrate up to 0022 (0023 and every later file removed: numbering must stay contiguous), add a period, then the rest.
        var later = TestPaths.MainMigrationFiles.Where(f => string.CompareOrdinal(f, "0023") >= 0).ToList();
        foreach (var file in later)
        {
            scratch.Delete(file);
        }

        await Db.Runner(cs).MigrateAsync(scratch.Source);
        await Db.ExecuteAsync(cs, """
            INSERT INTO md.company (company_id, rnc, legal_name) VALUES ('00000000-0000-7000-8000-00000000c001', '101000001', 'Empresa');
            INSERT INTO fin.period (period_id, company_id, starts_on, ends_on)
            VALUES ('00000000-0000-7000-8000-00000000c002', '00000000-0000-7000-8000-00000000c001', '2026-01-01', '2026-01-31');
            INSERT INTO fin.close_component_state (company_id, period_id, component, status, version)
            SELECT '00000000-0000-7000-8000-00000000c001', '00000000-0000-7000-8000-00000000c002', c, 'OPEN', 1 FROM (VALUES ('INV-MOV'), ('AP-REC')) AS v (c);
            """);

        foreach (var file in later)
        {
            File.Copy(Path.Combine(TestPaths.MainMigrations, file), Path.Combine(scratch.DirectoryPath, file));
        }

        await Db.Runner(cs).MigrateAsync(scratch.Source);

        Assert.Equal("ACR-NTX:OPEN,ACR-TAX:OPEN,AP-REC:OPEN,AR-REC:OPEN,BANK-REC:OPEN,COST-SET:OPEN,INV-MOV:OPEN,OP-DAY:OPEN", await Db.ScalarAsync<string>(cs, // ACR-*: FIN1-01; OP-DAY, COST-SET: MFG1-05
            "SELECT string_agg(component || ':' || status, ',' ORDER BY component) FROM fin.close_component_state"));
    }
}
