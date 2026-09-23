-- PR-01 · Frozen Baseline Patch 1, P-6:
-- btree_gist provides "=" operator classes for uuid/text inside GiST indexes, required by the
-- exclusion constraints on effective-date ranges (account_role_map, posting_rule_version,
-- accounting_policy_version, fiscal_rule_version, uom_conversion) created in later PRs.
CREATE EXTENSION IF NOT EXISTS btree_gist;
