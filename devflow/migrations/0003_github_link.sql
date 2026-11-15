-- Linking a person's GitHub account, so Dev flow holds a token that acts as
-- them on GitHub. How it works is "Linking GitHub" in devflow/README.md, and
-- why it is safe is "Acting as the owner" and "Security decisions and accepted
-- risks" in devflow/docs/devflow-architecture.md.

-- A link started and not yet returned from: the SHA-256 of the state sent to
-- the code host, never the state itself, and the PKCE verifier the challenge
-- was made from. Taken once, by the return, within ten minutes of created_at.
CREATE TABLE github_sign_ins (
    user_id uuid NOT NULL,
    state_hash bytea NOT NULL CHECK (octet_length(state_hash) = 32),
    -- 32 random bytes, base64url without padding.
    verifier text NOT NULL CHECK (verifier ~ '^[A-Za-z0-9_-]{43}$'),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (user_id, state_hash)
);

ALTER TABLE github_sign_ins ENABLE ROW LEVEL SECURITY;
ALTER TABLE github_sign_ins FORCE ROW LEVEL SECURITY;
CREATE POLICY github_sign_ins_user ON github_sign_ins
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- The one GitHub account a platform user has linked. Its two tokens are in
-- the vault, secrets, under the purposes github.access and github.refresh,
-- and nowhere else. needs_relink is a link whose renewal GitHub refused: its
-- tokens are gone, and the two times say when the last ones expired.
CREATE TABLE github_accounts (
    user_id uuid PRIMARY KEY,
    login text NOT NULL CHECK (login <> ''),
    github_user_id bigint NOT NULL,
    state text NOT NULL CHECK (state IN ('linked', 'needs_relink')),
    access_expires_at timestamptz NOT NULL,
    refresh_expires_at timestamptz NOT NULL,
    linked_at timestamptz NOT NULL
);

ALTER TABLE github_accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE github_accounts FORCE ROW LEVEL SECURITY;
CREATE POLICY github_accounts_user ON github_accounts
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
