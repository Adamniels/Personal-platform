-- A project's values: what its tests or its start need, a throwaway value or a key made for the
-- automation, never production's, which the owner gives on the project's page and every job that
-- runs the project's code carries into the environment of the agent, the check and the start. How
-- one is set, listed, carried and scrubbed is "Setting up a project", "Jobs" and "The worker" in
-- devflow/README.md, and why it is safe, with the risk accepted, "Security decisions and accepted
-- risks" in devflow/docs/devflow-architecture.md.
--
-- The value itself is sealed by the vault, as a secret is, AES-256-GCM under the key key_id names,
-- with a random 12 byte nonce and the 16 byte tag appended, and the user, the project and the name
-- as associated data, so a row moved to another of any of them does not open. It is kept here
-- rather than in secrets, so that disconnecting the project removes it with everything else of the
-- project's. No route ever answers it. The checks repeat the routes' own rules, in
-- src/DevFlow/Values/ProjectValues.cs, for a row written around them.
CREATE TABLE project_values (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    -- As the project reads it from its environment; reserved names are refused by the route.
    name text NOT NULL CHECK (name ~ '^[A-Z_][A-Z0-9_]*$' AND length(name) <= 100),
    -- The id of the key it was sealed with, as DEVFLOW_VAULT_KEY names it.
    key_id text NOT NULL CHECK (key_id ~ '^[a-z0-9]{1,16}$'),
    nonce bytea NOT NULL CHECK (octet_length(nonce) = 12),
    -- The tag is the last 16 bytes; a value is 8 to 4096 characters, so never empty.
    ciphertext bytea NOT NULL CHECK (octet_length(ciphertext) > 16),
    -- When the owner last gave it.
    set_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, name),
    -- Follows the project, gone with it and renumbered with it, as project_settings is.
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE project_values ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_values FORCE ROW LEVEL SECURITY;
CREATE POLICY project_values_user ON project_values
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
