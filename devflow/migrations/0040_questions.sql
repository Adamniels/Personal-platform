-- Questions before starting: with the setting on, a card its reading found questions in waits for
-- the owner's answers on "Waiting for you"; off, it goes on the reader's recommendations, as "The
-- reading job" built it. The route that changes it is "Settings" in devflow/README.md, and what it
-- does "Reading a card" there and in devflow/docs/devflow-architecture.md.

-- On for a new project, and for every project there is, by the default.
ALTER TABLE project_settings ADD COLUMN questions boolean NOT NULL DEFAULT true;
