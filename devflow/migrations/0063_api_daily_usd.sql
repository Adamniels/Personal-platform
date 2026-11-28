-- A project's daily cap on the API key, in whole US dollars: required on a project whose jobs may
-- carry the key, api_key and subscription_then_api_key, and null on the subscription alone. Its
-- runs on the key that finished since midnight in the user's zone are summed by cost_usd, Claude
-- Code's estimate, each time a worker asks for work; reached, the project starts nothing new on
-- the key until midnight, a raise, or, continuing on it, the subscription's reset. Never stored as
-- a hold. See "The API key" and "Settings" under "Cards" in devflow/README.md.

-- The checks repeat the route's, in src/DevFlow/Usage/ApiCap.cs, for a row written around it. A
-- project already on the key gets the cap the settings page proposes. The rows are every user's,
-- which the policy hides even from the owner while it is forced; it is lifted for this one
-- statement and forced again before the transaction ends, as 0041_presets.sql does.
ALTER TABLE project_settings ADD COLUMN api_daily_usd int CHECK (api_daily_usd BETWEEN 1 AND 1000);
ALTER TABLE project_settings NO FORCE ROW LEVEL SECURITY;
UPDATE project_settings SET api_daily_usd = 20 WHERE runs_on <> 'subscription';
ALTER TABLE project_settings FORCE ROW LEVEL SECURITY;
ALTER TABLE project_settings
    ADD CONSTRAINT project_settings_api_daily_usd_runs_on CHECK ((runs_on = 'subscription') = (api_daily_usd IS NULL));

-- Spend on the key is summed over the runs on it that finished today, apart from the rest.
CREATE INDEX runs_finished_on_the_key ON runs (project_id, finished_at) WHERE credential = 'api_key' AND finished_at IS NOT NULL;
