-- Tables review_owner creates in public from here on grant the app role what
-- review needs: SELECT, INSERT, UPDATE and DELETE, never TRUNCATE, which
-- empties a table without the row level security DELETE is held to. Sequences
-- grant USAGE, for an identity column or a serial. A table that needs less
-- revokes it in its own migration.
--
-- Applies only to tables and sequences created after this runs, and only those
-- created by review_owner, which runs every migration.
ALTER DEFAULT PRIVILEGES FOR ROLE review_owner IN SCHEMA public
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO review_app;
ALTER DEFAULT PRIVILEGES FOR ROLE review_owner IN SCHEMA public
GRANT USAGE ON SEQUENCES TO review_app;

-- Postgres 15 and later no longer let every role create in public, and this
-- says so here rather than leaning on the default: only the schema's owner,
-- the database's owner review_owner, creates tables, so review_app never owns
-- one and row level security always applies to it.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
