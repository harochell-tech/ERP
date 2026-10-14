-- MFG2-04 · Portal production from a date (E-MFG2-18): PRODUCTION policy parameter portal_import_from (YYYYMMDD; 0 = every day read).
-- Days before it are already in the opening inventory: Core opens no run and writes no draft for them.
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('portal_import_from', 'PRODUCTION', 'INTEGER', 0, 20991231, NULL,
   'Desde qué fecha (AAAAMMDD) Core toma la producción del portal; 0 = todos los días leídos (E-MFG2-18)',
   'Producción del portal desde', 'DATE', '13/10/2026',
   'La producción del portal de días anteriores a esa fecha no abre corridas ni borradores: ya está en el inventario de apertura. Con 0 se toma todo lo que el portal entrega.');
