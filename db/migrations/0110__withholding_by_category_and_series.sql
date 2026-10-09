-- X1-02 · ISR withholding by what is bought, ITBIS withholding by the supplier's document series (approved errata E-X1-8/9, E-X1-02-1…3).
--   - pur.expense_category.isr_withholding_type: the 606 ISR withholding type of the category's purchases (1 Alquileres, 2 Honorarios,
--     3 Otras rentas, 4 Rentas presuntas, …); an ISR withholding rule naming a type applies only to lines of categories of that type.
--     Set on a DRAFT category with its other fields, or on an ACTIVE one by the Controller.
--   - The B-series / e-CF scope of an ITBIS withholding rule lives in its definition (`document_series`), no column.

ALTER TABLE pur.expense_category
  ADD COLUMN isr_withholding_type text,
  ADD CONSTRAINT expense_category_isr_withholding_type CHECK (isr_withholding_type IS NULL OR isr_withholding_type ~ '^[1-9]$');

GRANT UPDATE (isr_withholding_type) ON pur.expense_category TO rochell_app;
