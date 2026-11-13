-- Tables brain_owner creates in public from here on grant the app role what
-- the brain needs at most: SELECT, INSERT and UPDATE. No DELETE, since the
-- service deletes nothing, see "What holds for every table" in
-- docs/data-model.md. No TRUNCATE either, since it empties a table without
-- needing DELETE. A table that needs less, events with no UPDATE for one,
-- revokes it in its own migration.
--
-- Applies only to tables created after this runs, and only those created by
-- brain_owner. Sequences are not covered, because the brain mints its own ids.
ALTER DEFAULT PRIVILEGES FOR ROLE brain_owner IN SCHEMA public
GRANT SELECT, INSERT, UPDATE ON TABLES TO brain_app;
