-- Workers: each project's one key, which every worker of the project holds, and the workers that
-- have introduced themselves with it. How a key is made and used is "Workers" in
-- devflow/README.md, and why worker_key_lookup is the second table without row level security is
-- "Security decisions and accepted risks" in devflow/docs/devflow-architecture.md.

-- A key and a worker belong to their project's user, by a foreign key on both columns rather than
-- by care in the code, since the lookup below leads from a key to that user. This is what the
-- foreign keys reference.
ALTER TABLE projects ADD UNIQUE (id, user_id);

-- A project's key, as its SHA-256 and nothing that gives the key back: the key is shown once, when
-- it is made, and never again. One per project; making a new one replaces the row, so the old key
-- stops working at once.
CREATE TABLE worker_keys (
    project_id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- SHA-256 of the key as written, dfw_ and the base64url of 32 random bytes.
    key_hash bytea NOT NULL UNIQUE CHECK (octet_length(key_hash) = 32),
    created_at timestamptz NOT NULL,
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE
);

ALTER TABLE worker_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE worker_keys FORCE ROW LEVEL SECURITY;
CREATE POLICY worker_keys_user ON worker_keys
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- Which project and user a key's hash belongs to, and nothing else. Deliberately without row level
-- security, so a worker's request, which carries no user, can be led from its key to its project's
-- user; everything after the lookup runs through the door as that user, and checks the hash again
-- against worker_keys there. Only the trigger below writes it.
CREATE TABLE worker_key_lookup (
    key_hash bytea PRIMARY KEY CHECK (octet_length(key_hash) = 32),
    project_id uuid NOT NULL UNIQUE,
    user_id uuid NOT NULL
);

-- The app role reads it and never writes it; the default privileges of
-- 0001_default_privileges.sql granted all four.
REVOKE INSERT, UPDATE, DELETE ON worker_key_lookup FROM devflow_app;

-- Keeps worker_key_lookup in step with worker_keys, so no code path can forget: a row for every
-- key, with its own hash, project and user, and none for a key replaced or a project
-- disconnected. Runs as devflow_owner, as sync_poll_target in 0004_projects.sql does, and for the
-- same reasons.
CREATE FUNCTION sync_worker_key_lookup() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
BEGIN
    IF TG_OP IN ('UPDATE', 'DELETE') THEN
        DELETE FROM public.worker_key_lookup WHERE project_id = OLD.project_id;
    END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') THEN
        INSERT INTO public.worker_key_lookup (key_hash, project_id, user_id)
        VALUES (NEW.key_hash, NEW.project_id, NEW.user_id);
    END IF;
    RETURN NULL;
END;
$$;

REVOKE ALL ON FUNCTION sync_worker_key_lookup() FROM PUBLIC;

CREATE TRIGGER worker_keys_lookup
    AFTER INSERT OR DELETE OR UPDATE OF key_hash, project_id, user_id ON worker_keys
    FOR EACH ROW EXECUTE FUNCTION sync_worker_key_lookup();

-- A worker that has said hello: the id it made when it started, and what it reported then. The
-- same id in two projects is two workers, so one user's worker can never collide with another's.
-- A worker not seen for ten minutes shows as not seen since its last_seen_at.
CREATE TABLE workers (
    project_id uuid NOT NULL,
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    -- The container's hostname, as the worker sends it.
    name text NOT NULL CHECK (name <> '' AND length(name) <= 255),
    recipe_version text NOT NULL CHECK (recipe_version <> '' AND length(recipe_version) <= 255),
    base_image text NOT NULL CHECK (base_image <> '' AND length(base_image) <= 255),
    first_seen_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, id),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE
);

ALTER TABLE workers ENABLE ROW LEVEL SECURITY;
ALTER TABLE workers FORCE ROW LEVEL SECURITY;
CREATE POLICY workers_user ON workers
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
