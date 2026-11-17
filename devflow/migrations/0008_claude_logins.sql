-- The Claude login: the token the owner makes with `claude setup-token` and gives Dev flow once,
-- which a run of his is handed. The token itself is in the vault, secrets, under the purpose
-- claude-login, and nowhere else; this table says only that there is one and when it was given,
-- since Claude Code cannot tell when a setup token runs out. How it is set, read and handed out is
-- "Claude login" in devflow/README.md, and why it is safe is "Security decisions and accepted
-- risks" in devflow/docs/devflow-architecture.md.

-- A purpose may now hold a hyphen, as claude-login does. The vault's own rule, in
-- src/DevFlow/Vault/Vault.cs, changes with it.
ALTER TABLE secrets DROP CONSTRAINT secrets_purpose_check;
ALTER TABLE secrets ADD CONSTRAINT secrets_purpose_check CHECK (purpose ~ '^[a-z][a-z0-9.-]*$');

-- One per user, not per project: the subscription is the user's. It references the sealed token by
-- the vault's own key, so a login cannot be recorded without its token, and removing the token
-- removes the login with it.
CREATE TABLE claude_logins (
    user_id uuid PRIMARY KEY,
    -- The vault's id of the sealed token, with user_id: always claude-login.
    secret_purpose text NOT NULL CHECK (secret_purpose = 'claude-login'),
    -- When the owner gave it. It is due for renewal 365 days later.
    given_at timestamptz NOT NULL,
    FOREIGN KEY (user_id, secret_purpose) REFERENCES secrets (user_id, purpose) ON DELETE CASCADE
);

ALTER TABLE claude_logins ENABLE ROW LEVEL SECURITY;
ALTER TABLE claude_logins FORCE ROW LEVEL SECURITY;
CREATE POLICY claude_logins_user ON claude_logins
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
