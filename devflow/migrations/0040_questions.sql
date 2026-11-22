-- Questions before starting: with the setting on, a card its reading found questions in waits for
-- the owner's answers on "Waiting for you"; off, it goes on the reader's recommendations, as "The
-- reading job" built it. The route that changes it is "Settings" in devflow/README.md, and what it
-- does "Reading a card" there and in devflow/docs/devflow-architecture.md.

-- On for a new project, and for every project there is, by the default.
ALTER TABLE project_settings ADD COLUMN questions boolean NOT NULL DEFAULT true;

-- A card its reading found questions in, with the setting on, needs the owner's answers: it waits
-- for them on "Waiting for you", nothing posted on GitHub, until he sends them. It is the owner's
-- turn, and inside a feature it holds back every later card, as a queued card does.
ALTER TABLE cards DROP CONSTRAINT cards_state_check;
ALTER TABLE cards ADD CONSTRAINT cards_state_check
    CHECK (state IN ('queued', 'reading', 'needs_answers', 'building', 'checking', 'reviewing', 'fixing', 'waiting_for_owner',
                     'done', 'stopped', 'refused', 'held', 'ignored'));
