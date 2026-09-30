using Rochell.Platform.Data;
using Rochell.Platform.Queries;

namespace Rochell.Reconciliation.Queries;

/// <summary>
/// E-UX3-3: a readable label for a finding's match key, resolved in SQL (the module graph lets Reconciliation depend on Platform
/// only). Recognised shapes: a party id (AP-GL, AR-GL) → legal name; area/item → area code / item code; stock:location/item/lot;
/// a PO line (GRNI-AGING); a value or GL entry (VALUE-GL-LINK); <c>GR:</c>, <c>GRR:</c>, <c>RC:</c>, <c>SI:</c>, <c>FA:</c>,
/// <c>NC:</c>, <c>REC:</c>, <c>BNC:</c>, <c>DEP:</c>, <c>RET:</c>, <c>CHG:</c> + id → the document number; <c>ap_doc:</c>,
/// <c>application:</c>, <c>APP:</c>, <c>line:</c> / <c>aged:</c> (delivery line), <c>collector:</c>, <c>auth-line:</c>,
/// <c>invoice-line:</c>, a component and a period id, <c>account:</c> code → code · name; a bank account id (BANK-GL) → bank and
/// masked number. Any other key (already readable ones such as <c>PAY:PAG-000001</c> included) has no label: null.
/// </summary>
public static class MatchLabels
{
    private const string U = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    private const string Sql = $$"""
        SELECT k.key, CASE
          WHEN @code IN ('AP-GL', 'AR-GL') AND k.key ~ '^{{U}}$' THEN
            (SELECT p.legal_name FROM md.party p WHERE p.company_id = @c AND p.party_id = k.key::uuid)
          WHEN @code IN ('INV-VALUE-GL', 'INV-VALUE-BALANCE', 'VAL-RESIDUAL') AND k.key ~ '^{{U}}/{{U}}$' THEN
            (SELECT a.code || ' / ' || i.code FROM md.valuation_area a, md.item i
             WHERE a.valuation_area_id = split_part(k.key, '/', 1)::uuid AND i.item_id = split_part(k.key, '/', 2)::uuid)
          WHEN k.key ~ '^area:{{U}}/{{U}}$' THEN
            (SELECT a.code || ' / ' || i.code FROM md.valuation_area a, md.item i
             WHERE a.valuation_area_id = split_part(substr(k.key, 6), '/', 1)::uuid AND i.item_id = split_part(substr(k.key, 6), '/', 2)::uuid)
          WHEN k.key ~ '^stock:{{U}}/{{U}}/{{U}}$' THEN
            (SELECT l.code || ' / ' || i.code || ' / ' || t.lot_code FROM md.location l, md.item i, inv.lot t
             WHERE l.location_id = split_part(substr(k.key, 7), '/', 1)::uuid AND i.item_id = split_part(substr(k.key, 7), '/', 2)::uuid
               AND t.lot_id = split_part(substr(k.key, 7), '/', 3)::uuid)
          WHEN @code = 'GRNI-AGING' AND k.key ~ '^{{U}}$' THEN
            (SELECT o.po_no || ' línea ' || l.line_no || ' · ' || i.code
             FROM pur.purchase_order_line l JOIN pur.purchase_order o ON o.po_id = l.po_id JOIN md.item i ON i.item_id = l.item_id
             WHERE l.company_id = @c AND l.po_line_id = k.key::uuid)
          WHEN @code = 'VALUE-GL-LINK' AND k.key ~ '^{{U}}$' THEN coalesce(
            (SELECT a.code || ' / ' || i.code FROM inv.inv_value_entry v JOIN md.valuation_area a ON a.valuation_area_id = v.valuation_area_id
             JOIN md.item i ON i.item_id = v.item_id WHERE v.company_id = @c AND v.value_entry_id = k.key::uuid),
            (SELECT a.code || ' · ' || a.name || ' · ' || to_char(e.posting_date, 'YYYY-MM-DD') FROM fin.gl_entry e JOIN fin.account a ON a.account_id = e.account_id
             WHERE e.company_id = @c AND e.gl_entry_id = k.key::uuid))
          WHEN k.key ~ '^GR:{{U}}$' THEN
            (SELECT g.gr_no FROM pur.goods_receipt g WHERE g.company_id = @c AND g.gr_id = substr(k.key, 4)::uuid)
          WHEN k.key ~ '^GRR:{{U}}$' THEN
            (SELECT 'Reversa de ' || g.gr_no FROM pur.goods_receipt_reversal r JOIN pur.goods_receipt g ON g.gr_id = r.reversed_gr_id
             WHERE r.company_id = @c AND r.grr_id = substr(k.key, 5)::uuid)
          WHEN k.key ~ '^RC:{{U}}$' THEN
            (SELECT 'Corrección de ' || g.gr_no FROM pur.receipt_correction r JOIN pur.goods_receipt g ON g.gr_id = r.gr_id
             WHERE r.company_id = @c AND r.rc_id = substr(k.key, 4)::uuid)
          WHEN k.key ~ '^SI:{{U}}$' THEN
            (SELECT s.supplier_fiscal_number || ' · ' || p.legal_name FROM pur.supplier_invoice s JOIN md.party p ON p.party_id = s.party_id
             WHERE s.company_id = @c AND s.si_id = substr(k.key, 4)::uuid)
          WHEN k.key ~ '^FA:{{U}}$' THEN
            (SELECT i.invoice_no FROM sal.invoice i WHERE i.company_id = @c AND i.invoice_id = substr(k.key, 4)::uuid)
          WHEN k.key ~ '^NC:{{U}}$' THEN
            (SELECT n.credit_note_no FROM sal.credit_note n WHERE n.company_id = @c AND n.credit_note_id = substr(k.key, 4)::uuid)
          WHEN k.key ~ '^(REC|BNC):{{U}}$' THEN
            (SELECT r.receipt_no FROM fin.receipt r WHERE r.company_id = @c AND r.receipt_id = substr(k.key, 5)::uuid)
          WHEN k.key ~ '^DEP:{{U}}$' THEN
            (SELECT d.deposit_no FROM fin.receipt_deposit d WHERE d.company_id = @c AND d.deposit_id = substr(k.key, 5)::uuid)
          WHEN k.key ~ '^RET:{{U}}$' THEN
            (SELECT 'Retención de ' || i.invoice_no FROM fin.customer_withholding w JOIN sal.invoice i ON i.invoice_id = w.invoice_id
             WHERE w.company_id = @c AND w.withholding_id = substr(k.key, 5)::uuid)
          WHEN k.key ~ '^CHG:{{U}}$' THEN
            (SELECT coalesce(l.bank_reference, l.description) || ' · ' || to_char(l.value_date, 'YYYY-MM-DD') FROM fin.bank_statement_line l
             WHERE l.company_id = @c AND l.line_id = substr(k.key, 5)::uuid)
          WHEN k.key ~ '^ap_doc:{{U}}$' THEN
            (SELECT s.supplier_fiscal_number || ' · ' || p.legal_name FROM fin.ap_document d
             JOIN pur.supplier_invoice s ON s.si_id = d.source_doc_id JOIN md.party p ON p.party_id = d.party_id
             WHERE d.company_id = @c AND d.ap_doc_id = substr(k.key, 8)::uuid)
          WHEN k.key ~ '^application:{{U}}$' THEN
            (SELECT p.payment_no FROM fin.ap_application a JOIN fin.payment p ON p.payment_id = a.payment_id
             WHERE a.company_id = @c AND a.application_id = substr(k.key, 13)::uuid)
          WHEN k.key ~ '^APP:{{U}}$' THEN
            (SELECT r.receipt_no FROM fin.ar_application a JOIN fin.receipt r ON r.receipt_id = a.receipt_id
             WHERE a.company_id = @c AND a.application_id = substr(k.key, 5)::uuid)
          WHEN k.key ~ '^(line|aged):{{U}}$' THEN
            (SELECT d.delivery_no || ' línea ' || l.line_no || ' · ' || i.code
             FROM log.delivery_line l JOIN log.delivery d ON d.delivery_id = l.delivery_id JOIN md.item i ON i.item_id = l.item_id
             WHERE l.company_id = @c AND l.delivery_line_id = split_part(k.key, ':', 2)::uuid)
          WHEN k.key ~ '^collector:{{U}}$' THEN
            (SELECT p.code || ' · ' || i.code || ' · ' || to_char(c.period_month, 'YYYY-MM')
             FROM mfg.cost_collector c JOIN md.plant p ON p.plant_id = c.plant_id JOIN md.item i ON i.item_id = c.item_id
             WHERE c.company_id = @c AND c.collector_id = substr(k.key, 11)::uuid)
          WHEN k.key ~ '^(OP-DAY|COST-SET):{{U}}$' THEN
            (SELECT split_part(k.key, ':', 1) || ' · ' || to_char(p.starts_on, 'YYYY-MM-DD') || ' a ' || to_char(p.ends_on, 'YYYY-MM-DD')
             FROM fin.period p WHERE p.company_id = @c AND p.period_id = split_part(k.key, ':', 2)::uuid)
          WHEN k.key ~ '^auth-line:{{U}}/[0-9]+$' THEN
            (SELECT a.certificate_no || ' línea ' || split_part(k.key, '/', 2) FROM tax.fiscal_authorization a
             WHERE a.company_id = @c AND a.authorization_id = split_part(substr(k.key, 11), '/', 1)::uuid)
          WHEN k.key ~ '^invoice-line:{{U}}$' THEN
            (SELECT i.invoice_no || ' línea ' || l.line_no FROM sal.invoice_line l JOIN sal.invoice i ON i.invoice_id = l.invoice_id
             WHERE i.company_id = @c AND l.invoice_line_id = substr(k.key, 14)::uuid)
          WHEN k.key ~ '^account:' THEN
            (SELECT a.code || ' · ' || a.name FROM fin.account a WHERE a.company_id = @c AND a.code = substr(k.key, 9))
          WHEN @code = 'BANK-GL' AND k.key ~ '^{{U}}:' THEN
            (SELECT b.bank_code || ' ' || CASE WHEN length(b.account_number) <= 4 THEN b.account_number ELSE '••••' || right(b.account_number, 4) END
                    || ' · ' || substr(k.key, 38)
             FROM fin.bank_account b WHERE b.company_id = @c AND b.bank_account_id = left(k.key, 36)::uuid)
        END
        FROM unnest(CAST(@keys AS text[])) AS k (key)
        """;

    /// <summary>The labels of the given keys of one reconciliation; a key without one is absent (or null).</summary>
    public static async Task<Dictionary<string, string?>> ResolveAsync(QueryContext context, string reconCode, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            Sql,
            r => (Key: r.GetString(0), Label: r.NullableString(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("code", reconCode),
            ("keys", keys.ToArray())).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Label, StringComparer.Ordinal);
    }
}
