-- PRS-04 · Freight on deliveries (E-SRV1-14, E-PRS-04-2 as corrected). P-16 version 2, DRAFT: version 1's lines plus the freight
-- lines — the freight of a delivered product line adds to the same contract asset (or unbilled receivable) of that delivery line
-- (E-PRS-01-3) and is credited to FREIGHT_REVENUE, without cost of sales. The Controller approves it (A-01); until then, and until
-- FREIGHT_REVENUE is mapped, orders carry no freight (they say why). From 2026-10-01; version 1 closes when version 2 is approved.
INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
SELECT v.posting_rule_id, 2,
       jsonb_set(v.definition, '{lines}', (v.definition -> 'lines') || '[
         {"code": "P16-DR-CA-FRT", "side": "DEBIT", "account_role": "CONTRACT_ASSET", "amount": "freight", "dimensions": ["party"], "subledger": "AR"},
         {"code": "P16-DR-UR-FRT", "side": "DEBIT", "account_role": "UNBILLED_RECEIVABLE", "amount": "freight", "dimensions": ["party"], "subledger": "AR"},
         {"code": "P16-CR-FRT", "side": "CREDIT", "account_role": "FREIGHT_REVENUE", "amount": "freight", "dimensions": ["plant", "party"]}
       ]'::jsonb),
       v.explanation_templates || '{
         "P16-DR-CA-FRT": "Flete entregado no facturado (activo de contrato) del conduce {delivery_no}: cantidad × flete de la zona del pedido {order_no}.",
         "P16-DR-UR-FRT": "Flete entregado no facturado (cuenta por cobrar no facturada) del conduce {delivery_no}: cantidad × flete de la zona del pedido {order_no}.",
         "P16-CR-FRT": "Ingreso por el transporte del conduce {delivery_no} (pedido {order_no}), reconocido al transferirse el control."
       }'::jsonb,
       v.close_component, v.also_requires_components, DATE '2026-10-01', 'DRAFT'
FROM fin.posting_rule_version v JOIN fin.posting_rule r ON r.posting_rule_id = v.posting_rule_id
WHERE r.code = 'P-16' AND v.version = 1;
