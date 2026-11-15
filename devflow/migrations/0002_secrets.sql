-- The vault's secrets: a value Dev flow holds for a user, such as a GitHub
-- token, sealed so that a copy of the database does not reveal it. What is
-- sealed, with what, and what it does and does not protect against is "The
-- vault" in devflow/README.md and "Security decisions and accepted risks" in
-- devflow/docs/devflow-architecture.md.
--
-- One row per user and purpose. The value is AES-256-GCM ciphertext with its
-- 16 byte tag appended, under the key key_id names and a random 12 byte nonce,
-- with the user and the purpose as associated data, so a row moved to another
-- user or purpose does not open. The checks repeat the vault's own rules, in
-- src/DevFlow/Vault/Vault.cs and VaultKey.cs, for a row written around it.
CREATE TABLE secrets (
    user_id uuid NOT NULL,
    -- A short fixed name the caller chooses, such as github.access.
    purpose text NOT NULL CHECK (purpose ~ '^[a-z][a-z0-9.]*$'),
    -- The id of the key it was sealed with, as DEVFLOW_VAULT_KEY names it.
    key_id text NOT NULL CHECK (key_id ~ '^[a-z0-9]{1,16}$'),
    nonce bytea NOT NULL CHECK (octet_length(nonce) = 12),
    -- The tag is the last 16 bytes.
    ciphertext bytea NOT NULL CHECK (octet_length(ciphertext) >= 16),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (user_id, purpose)
);

ALTER TABLE secrets ENABLE ROW LEVEL SECURITY;
ALTER TABLE secrets FORCE ROW LEVEL SECURITY;
CREATE POLICY secrets_user ON secrets
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
