-- The screenshots a waiting item's pull request body shows of its own, the
-- names RunnerReader accepted, in the body's order, each a string. The route
-- that serves one asks the code host only for a name listed here, so this is
-- what bounds what Dev flow fetches. See "Reading this repository's runner" in
-- devflow/docs/devflow-architecture.md.
ALTER TABLE waiting_items
    ADD COLUMN screenshots jsonb NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(screenshots) = 'array');
