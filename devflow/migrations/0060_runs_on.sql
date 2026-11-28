-- What a project runs on: the owner's subscription, through the Claude login, or the API key he gave
-- on the Projects tab, billed to his Anthropic account. Every job that runs an agent carries the one
-- credential its project runs on as it was when the job was handed out, and the worker gives Claude
-- Code that one and never both. See "Settings" under "Cards" and "The API key" in devflow/README.md.

-- Every project made before this ran on the subscription. The check repeats the route's values, in
-- src/DevFlow/Projects/RunsOn.cs, for a row written around the route; the next slice adds a third.
ALTER TABLE project_settings
    ADD COLUMN runs_on text NOT NULL DEFAULT 'subscription' CHECK (runs_on IN ('subscription', 'api_key'));

-- Each run records the credential its job carried, so spend on the key can be counted apart from
-- the subscription's. A check runs no agent and carries none; it keeps the default, and its cost is
-- none. Every run before this ran on the subscription.
ALTER TABLE runs
    ADD COLUMN credential text NOT NULL DEFAULT 'subscription' CHECK (credential IN ('subscription', 'api_key'));
