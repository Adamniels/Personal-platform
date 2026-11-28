-- A third thing a project runs on: the subscription, then the API key at its limit. Such a
-- project's jobs carry the subscription, and while the user's subscription is limited, in
-- subscription_limits, the API key; the first job handed out after the limit resets carries the
-- subscription again. See "Settings" under "Cards" and "The API key" in devflow/README.md.

-- The check repeats the route's values, in src/DevFlow/Projects/RunsOn.cs, for a row written
-- around the route. A run still records one of the two credentials its job carried.
ALTER TABLE project_settings DROP CONSTRAINT project_settings_runs_on_check;
ALTER TABLE project_settings
    ADD CONSTRAINT project_settings_runs_on_check CHECK (runs_on IN ('subscription', 'subscription_then_api_key', 'api_key'));
