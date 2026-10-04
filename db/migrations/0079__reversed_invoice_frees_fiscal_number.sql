-- GAS1-04 · A reversed supplier invoice frees its fiscal number (approved errata E-GAS-04-6): the document was registered by
-- mistake and has been fully undone, so the same NCF of the same supplier can be registered again — as a voided one already could.
DROP INDEX pur.si_fiscal_uq;
CREATE UNIQUE INDEX si_fiscal_uq ON pur.supplier_invoice (company_id, party_id, supplier_fiscal_number) WHERE document_status NOT IN ('VOIDED', 'REVERSED');
