-- The API key: an Anthropic API key the owner gives Dev flow once, on the Projects tab, which a run
-- of a project that runs on it is handed in place of the Claude login, so the run is billed to his
-- Anthropic account rather than drawn from his subscription. The key itself is in the vault,
-- secrets, under the purpose anthropic-api-key, and nowhere else; this table says only that there
-- is one and when it was given. How it is set, read and handed out is "The API key" in
-- devflow/README.md, and why it is safe is "Security decisions and accepted risks" in
-- devflow/docs/devflow-architecture.md.

-- One per user, not per project, as the Claude login is: one owner pays for every project of his.
-- It references the sealed key by the vault's own key, so a key cannot be recorded without its
-- secret, and removing the secret removes the record with it.
CREATE TABLE api_keys (
    user_id uuid PRIMARY KEY,
    -- The vault's id of the sealed key, with user_id: always anthropic-api-key.
    secret_purpose text NOT NULL CHECK (secret_purpose = 'anthropic-api-key'),
    -- When the owner gave it. An API key does not run out by itself, so nothing is due.
    given_at timestamptz NOT NULL,
    FOREIGN KEY (user_id, secret_purpose) REFERENCES secrets (user_id, purpose) ON DELETE CASCADE
);

ALTER TABLE api_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE api_keys FORCE ROW LEVEL SECURITY;
CREATE POLICY api_keys_user ON api_keys
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
