-- Projects: a repository a user connected, and the polling targets a loop with
-- no logged in user reads to learn which projects exist and whose they are.
-- How connecting works is "Connecting a repository" in devflow/README.md, and
-- why poll_targets is the one table without row level security is "Security
-- decisions and accepted risks" in devflow/docs/devflow-architecture.md.

-- One connected repository with its settings, which in this part are none
-- but bot_login. The owner and name are the code host's when it was
-- connected, for display; the repository is identified by its installation
-- and id, which stay when it is renamed. Everything a later slice stores for a
-- project references id with ON DELETE CASCADE, so disconnecting removes it.
CREATE TABLE projects (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    code_host text NOT NULL CHECK (code_host = 'github'),
    installation_id bigint NOT NULL,
    repository_id bigint NOT NULL,
    owner text NOT NULL CHECK (owner <> ''),
    name text NOT NULL CHECK (name <> ''),
    -- The login whose pull requests the runner's reader looks at, this
    -- repository's runner's bot unless a later part lets it change.
    bot_login text NOT NULL DEFAULT 'mac-mini-agent' CHECK (bot_login <> ''),
    created_at timestamptz NOT NULL,
    UNIQUE (user_id, repository_id)
);

ALTER TABLE projects ENABLE ROW LEVEL SECURITY;
ALTER TABLE projects FORCE ROW LEVEL SECURITY;
CREATE POLICY projects_user ON projects
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- Which projects exist and whose they are, and nothing else: no name, no
-- repository, no token. Deliberately without row level security, so a loop
-- that has no logged in user can read it, and then does all its work through
-- the door as each project's user. Only the trigger below writes it.
CREATE TABLE poll_targets (
    project_id uuid PRIMARY KEY,
    user_id uuid NOT NULL
);

-- The app role reads it and never writes it; the default privileges of
-- 0001_default_privileges.sql granted all four.
REVOKE INSERT, UPDATE, DELETE ON poll_targets FROM devflow_app;

-- Keeps poll_targets in step with projects, so no code path can forget: a
-- target for every project, with the project's own id and user. It runs as
-- devflow_owner, who owns it and the table, since the app role that writes
-- projects cannot write poll_targets. Its search_path is fixed, as a
-- SECURITY DEFINER function's must be, so nothing the caller sets is used.
CREATE FUNCTION sync_poll_target() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
BEGIN
    IF TG_OP IN ('UPDATE', 'DELETE') THEN
        DELETE FROM public.poll_targets WHERE project_id = OLD.id;
    END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') THEN
        INSERT INTO public.poll_targets (project_id, user_id) VALUES (NEW.id, NEW.user_id);
    END IF;
    RETURN NULL;
END;
$$;

REVOKE ALL ON FUNCTION sync_poll_target() FROM PUBLIC;

CREATE TRIGGER projects_poll_target
    AFTER INSERT OR DELETE OR UPDATE OF id, user_id ON projects
    FOR EACH ROW EXECUTE FUNCTION sync_poll_target();
