-- X1-02b · the tax category of a fixed-asset class (approved errata E-X1-18, E-X1-02-4).
--   1 buildings and their structural components, 2 light vehicles, office equipment and computers, 3 any other depreciable property
--   (Código Tributario art. 287 e). On an expense invoice of a category-1 asset its ITBIS is not deductible (art. 336 Párrafo): it is
--   NON_RECOVERABLE_INPUT and adds to the asset's cost. A class keeps the category it was prepared with; classes prepared before have none
--   (their ITBIS stays deductible) until a new version names it.

ALTER TABLE fa.asset_class
  ADD COLUMN tax_category smallint,
  ADD CONSTRAINT asset_class_tax_category CHECK (tax_category IS NULL OR tax_category IN (1, 2, 3));
