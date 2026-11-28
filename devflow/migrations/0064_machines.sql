-- Machines: each of a user's machines that runs the machine program, its key, and the one-time
-- codes a machine is enrolled with. How a machine enrols and calls Dev flow is "Machines" in
-- devflow/README.md, and why machine_key_lookup and machine_enrollment_lookup are the third and
-- fourth tables without row level security is "Security decisions and accepted risks" in
-- devflow/docs/devflow-architecture.md. The keys and their lookup copy worker_keys and
-- worker_key_lookup in 0007_workers.sql.

-- A machine that enrolled with a code of its user's: its name, OS and architecture as it sent them
-- then, and from its first hello on, the program's version and Docker's state, with when it was
-- first and last seen. It is online while it was seen in the last minute.
CREATE TABLE machines (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    name text NOT NULL CHECK (name <> '' AND length(name) <= 255),
    os text NOT NULL CHECK (os IN ('macos', 'linux')),
    arch text NOT NULL CHECK (arch IN ('arm64', 'x64')),
    -- Both null until the machine's first hello, and both set by it.
    program_version text CHECK (program_version <> '' AND length(program_version) <= 255),
    -- 'running', or why Docker is not, as the program reported it.
    docker text CHECK (docker <> '' AND length(docker) <= 255),
    first_seen_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    CHECK ((program_version IS NULL) = (docker IS NULL)),
    -- What machine_keys references, so a key can only belong to its machine's own user.
    UNIQUE (id, user_id)
);

ALTER TABLE machines ENABLE ROW LEVEL SECURITY;
ALTER TABLE machines FORCE ROW LEVEL SECURITY;
CREATE POLICY machines_user ON machines
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- A machine's key, as its SHA-256 and nothing that gives the key back: the key is answered once,
-- when the machine enrols, and never again. One per machine; removing the machine removes it, so
-- the key stops working at once.
CREATE TABLE machine_keys (
    machine_id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- SHA-256 of the key as written, dfm_ and the base64url of 32 random bytes.
    key_hash bytea NOT NULL UNIQUE CHECK (octet_length(key_hash) = 32),
    created_at timestamptz NOT NULL,
    FOREIGN KEY (machine_id, user_id) REFERENCES machines (id, user_id) ON DELETE CASCADE
);

ALTER TABLE machine_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE machine_keys FORCE ROW LEVEL SECURITY;
CREATE POLICY machine_keys_user ON machine_keys
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- Which machine and user a key's hash belongs to, and nothing else. Without row level security, so
-- a machine's request, which carries no user, can be led from its key to its user; everything after
-- the lookup runs through the door as that user, and checks the hash again against machine_keys
-- there. Only the trigger below writes it.
CREATE TABLE machine_key_lookup (
    key_hash bytea PRIMARY KEY CHECK (octet_length(key_hash) = 32),
    machine_id uuid NOT NULL UNIQUE,
    user_id uuid NOT NULL
);

-- The app role reads it and never writes it; the default privileges of
-- 0001_default_privileges.sql granted all four.
REVOKE INSERT, UPDATE, DELETE ON machine_key_lookup FROM devflow_app;

-- Keeps machine_key_lookup in step with machine_keys, so no code path can forget: a row for every
-- key, with its own hash, machine and user, and none for a machine removed. Runs as devflow_owner,
-- as sync_worker_key_lookup in 0007_workers.sql does, and for the same reasons.
CREATE FUNCTION sync_machine_key_lookup() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
BEGIN
    IF TG_OP IN ('UPDATE', 'DELETE') THEN
        DELETE FROM public.machine_key_lookup WHERE machine_id = OLD.machine_id;
    END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') THEN
        INSERT INTO public.machine_key_lookup (key_hash, machine_id, user_id)
        VALUES (NEW.key_hash, NEW.machine_id, NEW.user_id);
    END IF;
    RETURN NULL;
END;
$$;

REVOKE ALL ON FUNCTION sync_machine_key_lookup() FROM PUBLIC;

CREATE TRIGGER machine_keys_lookup
    AFTER INSERT OR DELETE OR UPDATE OF key_hash, machine_id, user_id ON machine_keys
    FOR EACH ROW EXECUTE FUNCTION sync_machine_key_lookup();

-- A code "Add a machine" made, as its SHA-256: valid for an hour from created_at, and once, until
-- used_at is set. A newer code ends no older one. Making a code deletes its user's codes made an
-- hour ago or more, used or not, so the table holds about an hour of them.
CREATE TABLE machine_enrollments (
    -- SHA-256 of the code as written, dfe_ and the base64url of 16 random bytes.
    code_hash bytea PRIMARY KEY CHECK (octet_length(code_hash) = 32),
    user_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    used_at timestamptz CHECK (used_at >= created_at)
);

ALTER TABLE machine_enrollments ENABLE ROW LEVEL SECURITY;
ALTER TABLE machine_enrollments FORCE ROW LEVEL SECURITY;
CREATE POLICY machine_enrollments_user ON machine_enrollments
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- Which user a code's hash belongs to, and nothing else: no time and no use, which the enrolment
-- reads from machine_enrollments as that user. Without row level security, for the same reason as
-- machine_key_lookup, since enrolling is the one machine request that carries neither a user nor a
-- key. Only the trigger below writes it.
CREATE TABLE machine_enrollment_lookup (
    code_hash bytea PRIMARY KEY CHECK (octet_length(code_hash) = 32),
    user_id uuid NOT NULL
);

REVOKE INSERT, UPDATE, DELETE ON machine_enrollment_lookup FROM devflow_app;

CREATE FUNCTION sync_machine_enrollment_lookup() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
BEGIN
    IF TG_OP IN ('UPDATE', 'DELETE') THEN
        DELETE FROM public.machine_enrollment_lookup WHERE code_hash = OLD.code_hash;
    END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') THEN
        INSERT INTO public.machine_enrollment_lookup (code_hash, user_id)
        VALUES (NEW.code_hash, NEW.user_id);
    END IF;
    RETURN NULL;
END;
$$;

REVOKE ALL ON FUNCTION sync_machine_enrollment_lookup() FROM PUBLIC;

CREATE TRIGGER machine_enrollments_lookup
    AFTER INSERT OR DELETE OR UPDATE OF code_hash, user_id ON machine_enrollments
    FOR EACH ROW EXECUTE FUNCTION sync_machine_enrollment_lookup();
