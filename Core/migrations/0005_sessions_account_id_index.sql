-- Revoking every session of an account, as a password reset does, finds its
-- rows by this. Planning the sessions table left the index to the first flow
-- that finds sessions by account. See "Password reset, and changing a
-- password" in docs/core-architecture.md.

CREATE INDEX sessions_account_id ON sessions (account_id);
