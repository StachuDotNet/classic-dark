--#[no_tx]
-- ALTER TYPE .. ADD VALUE cannot be run in a transaction or in multi-line statements.
-- see https://www.postgresql.org/docs/13/sql-altertype.html
ALTER TYPE queue_status ADD VALUE IF NOT EXISTS 'missing'