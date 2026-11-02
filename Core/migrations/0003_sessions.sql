-- The refresh side of the token model, in its final shape. See "The core's
-- data" and "The session" in docs/core-architecture.md. The app role gets
-- SELECT, INSERT and UPDATE from 0001's default privileges, and no DELETE:
-- rotation marks a row used and logout marks it revoked, so a used row stays
-- to make reuse detectable.
--
-- No defaults on ids or times: the core mints every UUIDv7, the family id
-- included, and passes its own clock.

CREATE TABLE sessions (
    id                uuid        PRIMARY KEY,
    account_id        uuid        NOT NULL REFERENCES accounts (id),
    -- The session id in the access token's sid claim; what logout, reuse
    -- detection and a password change revoke.
    family_id         uuid        NOT NULL,
    -- SHA-256 of the refresh token's 32 random bytes; tokens are found by it.
    token_hash        bytea       NOT NULL UNIQUE CHECK (length(token_hash) = 32),
    issued_at         timestamptz NOT NULL,
    expires_at        timestamptz NOT NULL,
    -- Set at login and copied forward on each rotation, so a refresh respects
    -- the thirty day cap by reading one row.
    family_expires_at timestamptz NOT NULL,
    used_at           timestamptz,
    revoked_at        timestamptz,
    CHECK (expires_at > issued_at),
    CHECK (expires_at <= family_expires_at)
);

-- Revoking a family, by reuse detection or logout, finds its rows by this.
CREATE INDEX sessions_family_id ON sessions (family_id);
