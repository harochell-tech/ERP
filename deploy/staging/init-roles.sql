-- Login roles of the staging database (E-B03-6), run by deploy.sh after the migrations with psql variables
-- app_password and sealer_password. Idempotent: creates the logins once and resets their passwords on every deploy.
\set ON_ERROR_STOP on
SELECT format('CREATE ROLE rochell_app_login LOGIN IN ROLE rochell_app')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rochell_app_login') \gexec
SELECT format('CREATE ROLE rochell_sealer_login LOGIN IN ROLE rochell_sealer')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rochell_sealer_login') \gexec
SELECT format('ALTER ROLE rochell_app_login PASSWORD %L', :'app_password') \gexec
SELECT format('ALTER ROLE rochell_sealer_login PASSWORD %L', :'sealer_password') \gexec
