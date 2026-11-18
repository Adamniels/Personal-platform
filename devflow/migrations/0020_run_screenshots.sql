-- A build's pictures: after its check passed and its branch was pushed, the worker takes the pages
-- its report names and commits them to the branch pr-screenshots, under
-- devflow/<card>/<the head commit's first seven characters>/<name>.png. The run keeps their names
-- and the worker's note, and the card's page and its pull request read them from here, never from
-- the code host. See "Screenshots" in devflow/README.md, and "The worker's pictures" in
-- devflow/docs/devflow-architecture.md for why they decide nothing.

-- The names, in the report's order, each lowercase letters, digits and hyphens ending in -desktop
-- or -phone, as the worker's result checked them; the route that serves one asks the code host only
-- for a name listed here. Null for a run that took none: a review, a failed check, a worker that
-- sends none, and every run before this column.
ALTER TABLE runs ADD COLUMN screenshots jsonb CHECK (screenshots IS NULL OR jsonb_typeof(screenshots) = 'array');

-- What the worker said instead or as well: "This project has no pages to show", "The report names
-- no pages", or "Screenshots could not be taken: <why>". Null when every page was taken, or none
-- was tried.
ALTER TABLE runs ADD COLUMN screenshots_note text CHECK (screenshots_note <> '' AND length(screenshots_note) <= 1000);
