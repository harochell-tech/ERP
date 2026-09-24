using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>Supplier invoice guarantees in the database: §11.4 transitions and invalid combinations, NCF uniqueness, ADR-027, privileges, RLS.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierInvoiceSchemaTests(PostgresFixture postgres)
{
    private static async Task<(TestHarness H, Guid Si)> RegisteredAsync(PostgresFixture postgres)
    {
        var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "reg", s.Purchasing.SupplierId, "B0100000001", today, today.AddDays(30), [new(s.PoLineId, 6m, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        return (h, si);
    }

    [Theory]
    [InlineData("UPDATE pur.supplier_invoice SET document_status = 'REVERSED', accounting_status = 'REVERSED', version = version + 1")]
    [InlineData("UPDATE pur.supplier_invoice SET total_amount = 1, version = version + 1")]
    [InlineData("UPDATE pur.supplier_invoice SET version = version")]
    [InlineData("DELETE FROM pur.supplier_invoice")]
    [InlineData("UPDATE pur.supplier_invoice_line SET qty = qty")]
    public async Task Invoices_are_protected_even_for_the_owner(string sql)
    {
        var (h, _) = await RegisteredAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE pur.supplier_invoice SET accounting_status = 'POSTED', version = version + 1")]                 // DRAFT + POSTED
    [InlineData("UPDATE pur.supplier_invoice SET exception_approved_by = created_by, version = version + 1")]          // approver = registrar
    [InlineData("UPDATE pur.supplier_invoice SET accounting_status = 'POSTING_BLOCKED', version = version + 1")]       // blocked only when MATCHED
    public async Task Invalid_combinations_are_check_violations(string sql)
    {
        var (h, _) = await RegisteredAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.CheckViolation, (await h.AdminExecuteAsync(sql))?.SqlState);
        }
    }

    [Fact]
    public async Task The_fiscal_number_is_unique_per_supplier_among_non_voided_invoices()
    {
        var (h, si) = await RegisteredAsync(postgres);
        await using (h)
        {
            var ex = await h.AdminExecuteAsync(
                $"""
                INSERT INTO pur.supplier_invoice (si_id, company_id, party_id, supplier_fiscal_number, doc_date, due_date, document_status, accounting_status, total_amount, created_by, version)
                SELECT gen_random_uuid(), company_id, party_id, supplier_fiscal_number, doc_date, due_date, 'DRAFT', 'NOT_POSTED', total_amount, created_by, 1
                FROM pur.supplier_invoice WHERE si_id = '{si}'
                """);

            Assert.Equal(SqlStates.UniqueViolation, ex?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE pur.supplier_invoice SET total_amount = total_amount")]
    [InlineData("UPDATE pur.supplier_invoice_line SET qty = qty")]
    [InlineData("DELETE FROM pur.supplier_invoice")]
    [InlineData("UPDATE fin.ap_document SET original_amount = original_amount")]
    public async Task Application_role_cannot_bypass_the_commands(string sql)
    {
        var (h, _) = await RegisteredAsync(postgres);
        await using (h)
        {
            Assert.Equal("42501", (await h.AppExecuteAsync(sql))?.SqlState);
        }
    }

    [Fact]
    public async Task Row_level_security_isolates_invoices()
    {
        var (h, _) = await RegisteredAsync(postgres);
        await using (h)
        {
            var other = await h.CreateCompanyAsync();
            var (connection, tx) = await h.OpenAppTransactionAsync(other);
            await using (connection)
            await using (tx)
            {
                await using var count = new Npgsql.NpgsqlCommand("SELECT (SELECT count(*) FROM pur.supplier_invoice) + (SELECT count(*) FROM pur.supplier_invoice_line)", connection, tx);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }
        }
    }
}
