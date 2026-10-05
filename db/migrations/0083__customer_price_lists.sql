-- PRS-02 · E-PRS-02-6: a quote line records the list version its list price came from (the customer's list or GENERAL), as the order
-- line does since 0082. Lines written before this migration keep it empty.
ALTER TABLE sal.quote_line
  ADD COLUMN price_list_version_id uuid,
  ADD CONSTRAINT quote_line_price_list_fk FOREIGN KEY (company_id, price_list_version_id) REFERENCES sal.price_list_version (company_id, price_list_version_id);
