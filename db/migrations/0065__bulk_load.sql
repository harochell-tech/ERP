-- IMP-01 · Bulk load of suppliers and customers (approved errata E-IMP-1…11, E-IMP-01-1…8): several e-mails per party and the
-- permissions of the two import commands. The files themselves are read by the application; nothing here holds real data.

-- E-IMP-6, E-IMP-01-3: up to ten e-mails per party, in the order given; position 1 is the principal one and is also kept in
-- md.party.email, which every existing query reads. The commands replace a party's list as a whole (DELETE + INSERT under the
-- party's row lock), so the table carries no version of its own.
CREATE TABLE md.party_email (
  company_id uuid     NOT NULL,
  party_id   uuid     NOT NULL,
  position   smallint NOT NULL,
  email      text     NOT NULL,
  CONSTRAINT party_email_pk PRIMARY KEY (company_id, party_id, position),
  CONSTRAINT party_email_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT party_email_position CHECK (position BETWEEN 1 AND 10),
  CONSTRAINT party_email_format CHECK (email ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$' AND length(email) <= 200)
);
CREATE UNIQUE INDEX party_email_address_uq ON md.party_email (company_id, party_id, lower(email));

INSERT INTO md.party_email (company_id, party_id, position, email)
SELECT company_id, party_id, 1, email FROM md.party WHERE email IS NOT NULL;

ALTER TABLE md.party_email ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON md.party_email
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT, DELETE ON md.party_email TO rochell_app;

-- E-IMP-10, E-IMP-01-4: the imports are commands of their own, held by the roles that create suppliers or customers (SUPERADMIN
-- receives every new permission through its trigger, E-ADM-2). E-IMP-01-5: who imports does not activate.
INSERT INTO iam.permission (permission_code, access) VALUES ('supplier:import', 'WRITE'), ('customer:import', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT rp.role_id, v.import_code
FROM (VALUES ('supplier:create', 'supplier:import'), ('customer:create', 'customer:import')) AS v (create_code, import_code)
JOIN iam.role_permission rp ON rp.permission_code = v.create_code
ON CONFLICT DO NOTHING;

INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES
  ('supplier:import', 'supplier:activate'),
  ('customer:import', 'customer:activate')
) AS v (a, b);
