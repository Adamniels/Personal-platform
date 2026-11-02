-- Accounts, and the invite codes that create them, in their final shape. See
-- "The core's data" and "The user id" in docs/core-architecture.md. The app
-- role gets SELECT, INSERT and UPDATE on both from 0001's default privileges.
--
-- No defaults on ids or times: the core mints every UUIDv7 and passes its own
-- clock, so there is one source of each.

CREATE TABLE accounts (
    id            uuid        PRIMARY KEY,
    -- Lowercased by the core, and refused here if it was not, so a path that
    -- forgot cannot store a second spelling of one address.
    email         text        NOT NULL UNIQUE CHECK (email = lower(email)),
    -- A PHC string, which carries its own Argon2id parameters.
    password_hash text        NOT NULL,
    -- A check rather than an enum, since a check can lose a value later.
    status        text        NOT NULL CHECK (status IN ('active', 'disabled')),
    created_at    timestamptz NOT NULL
);

CREATE TABLE invite_codes (
    -- SHA-256 of the code's 32 random bytes; codes are found by it.
    code_hash  bytea       PRIMARY KEY CHECK (length(code_hash) = 32),
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > created_at),
    used_at    timestamptz,
    -- Deferred to commit, so registration can consume the code naming an
    -- account it inserts a moment later in the same transaction, and a taken
    -- email is only ever reported to someone holding a valid code.
    used_by    uuid        UNIQUE REFERENCES accounts (id) DEFERRABLE INITIALLY DEFERRED,
    CHECK ((used_at IS NULL) = (used_by IS NULL))
);
