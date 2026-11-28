-- Claude's usage limit is the user's, since the subscription is: when one run of any project of the
-- user finds it, every project of theirs on the subscription waits for its reset, not only the one
-- whose run found it. A row says until when; it is set by a run that failed outside its card with
-- a reset after now and at most 8 days off, the later of two kept, and never cleared: once its
-- reset has passed it holds nothing. See "Stops" and "The API key" in devflow/README.md, and why
-- the limit is the user's, "Stops" in devflow/docs/devflow-architecture.md.
CREATE TABLE subscription_limits (
    user_id uuid PRIMARY KEY,
    -- When the subscription's limit resets, as Claude Code's answer said.
    resets_at timestamptz NOT NULL,
    -- When the run that said so was recorded.
    recorded_at timestamptz NOT NULL
);

ALTER TABLE subscription_limits ENABLE ROW LEVEL SECURITY;
ALTER TABLE subscription_limits FORCE ROW LEVEL SECURITY;
CREATE POLICY subscription_limits_user ON subscription_limits
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
