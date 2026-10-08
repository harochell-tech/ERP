-- PRT-02 · E-PRT-9: the Director changes the print formats (print_format:manage, granted in 0102), so the role is no longer read-only.
UPDATE iam.role
SET name = 'Director',
    description = 'Consulta todas las pantallas y reportes y cambia los formatos de impresión; no ejecuta otras operaciones.'
WHERE code = 'DIRECTOR';
