-- A project's daily budget in tokens, off by default. Reached, the project starts nothing new until
-- midnight in its user's zone, or until the budget is raised or switched off: the hold is worked out
-- from the runs each time it is asked for and never stored. What counts, and the hold's place among
-- the reasons a project waits, is "Settings" under "Cards" and "Stops" in devflow/README.md; why it
-- is worked out, "Stops" in devflow/docs/devflow-architecture.md.

-- Null for off. On the wire it is tokens, and the page takes it in millions, a whole number from 1
-- to 10 000; the route refuses anything else, in src/DevFlow/Usage/DailyBudget.cs, and the check
-- here repeats it for a row written around the route.
ALTER TABLE project_settings ADD COLUMN daily_token_budget bigint
    CHECK (daily_token_budget BETWEEN 1000000 AND 10000000000 AND daily_token_budget % 1000000 = 0);

-- The tokens a project's runs used on a day are summed over the runs that finished in it, every
-- time a worker asks for work; the index keeps that a range read as the runs pile up.
CREATE INDEX runs_finished ON runs (project_id, finished_at) WHERE finished_at IS NOT NULL;
