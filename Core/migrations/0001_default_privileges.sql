-- Tables core_owner creates in public from here on grant the app role what
-- the core needs: SELECT, INSERT and UPDATE. No DELETE, see "The application
-- role is granted no DELETE" in docs/core-architecture.md. No TRUNCATE either,
-- since it empties a table without needing DELETE. A table that genuinely
-- needs deleting gets its grant in a migration of its own.
--
-- Applies only to tables created after this runs, and only those created by
-- core_owner. Sequences are not covered, because the core mints its own ids.
ALTER DEFAULT PRIVILEGES FOR ROLE core_owner IN SCHEMA public
GRANT SELECT, INSERT, UPDATE ON TABLES TO core_app;
