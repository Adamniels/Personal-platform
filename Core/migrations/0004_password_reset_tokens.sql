-- Password reset tokens, in their final shape. See "Password reset, and
-- changing a password" and "The core's data" in docs/core-architecture.md.
-- The invite code shape with the account the token is for, since a reset
-- token belongs to an account from the start. The app role gets SELECT,
-- INSERT and UPDATE from 0001's default privileges, and no DELETE: consuming
-- marks a row used, so a used token stays to show it was.
--
-- No defaults on times: the core passes its own clock, so there is one source
-- of it.

CREATE TABLE password_reset_tokens (
    -- SHA-256 of the token's 32 random bytes; tokens are found by it.
    token_hash bytea       PRIMARY KEY CHECK (length(token_hash) = 32),
    account_id uuid        NOT NULL REFERENCES accounts (id),
    created_at timestamptz NOT NULL,
    -- The first instant the token no longer works, an hour after it was made.
    expires_at timestamptz NOT NULL CHECK (expires_at > created_at),
    used_at    timestamptz
);
