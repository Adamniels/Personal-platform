-- Setting up a project: every version of a project's .devflow/ is checked once, before its workers
-- take any other job, by a check job a worker runs on a clean clone of the default branch's head.
-- How a check is handed out and judged is "Setting up a project" in devflow/README.md, and why it is
-- built so "Setting up a project" in devflow/docs/devflow-architecture.md.

-- A check is a run of its own, of no card and no feature: it names the setup version it checks, the
-- worker's, and the commit it checks in from_commit, and pushes nothing.
ALTER TABLE runs DROP CONSTRAINT runs_kind_check;
ALTER TABLE runs ADD CONSTRAINT runs_kind_check
    CHECK (kind IN ('build', 'review', 'fix', 'conflict', 'feature_review', 'feature_fix', 'read', 'check'));
ALTER TABLE runs DROP CONSTRAINT runs_card_or_feature;
ALTER TABLE runs ADD CONSTRAINT runs_card_or_feature CHECK (
    CASE WHEN kind = 'check' THEN card_number IS NULL AND feature_number IS NULL
         ELSE (card_number IS NULL) <> (feature_number IS NULL) END);
ALTER TABLE runs ADD COLUMN recipe_version text CHECK (recipe_version <> '' AND length(recipe_version) <= 255);
ALTER TABLE runs ADD CONSTRAINT runs_check_names_its_version CHECK ((kind = 'check') = (recipe_version IS NOT NULL));
ALTER TABLE runs ADD CHECK (kind <> 'check' OR (from_commit IS NOT NULL AND head_commit IS NULL));

-- One check of a version under way at a time, whatever the code does: the claim reads it under the
-- project's lock first, and this is the backstop.
CREATE UNIQUE INDEX runs_one_check_under_way ON runs (project_id, recipe_version) WHERE kind = 'check' AND finished_at IS NULL;

-- What each check that ran found: the version and the commit it checked, whether it passed, the
-- check's exit code and its last 200 lines, the names of the pictures it took of the project's
-- address, or why there are none, and when. A version counts as checked once one check of it
-- passed; a project is registered once any version has. A check that failed outside the project,
-- the network or the machine, leaves no row: it is no verdict on the setup. When the owner asks for
-- another check of a version whose check failed, again_at says when. It names its run by id, as a
-- card's readings do, and goes with its project.
CREATE TABLE recipe_checks (
    run_id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    version text NOT NULL CHECK (version <> '' AND length(version) <= 255),
    commit text NOT NULL CHECK (commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    passed boolean NOT NULL,
    exit_code int NOT NULL,
    output text NOT NULL,
    pictures text[] NOT NULL DEFAULT '{}',
    pictures_note text CHECK (pictures_note <> '' AND length(pictures_note) <= 1000),
    checked_at timestamptz NOT NULL,
    again_at timestamptz,
    CHECK (passed = (exit_code = 0)),
    CHECK (again_at IS NULL OR NOT passed),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

-- A version's checks, newest first, which handing out and the project's page read.
CREATE INDEX recipe_checks_version ON recipe_checks (project_id, version, checked_at DESC);

ALTER TABLE recipe_checks ENABLE ROW LEVEL SECURITY;
ALTER TABLE recipe_checks FORCE ROW LEVEL SECURITY;
CREATE POLICY recipe_checks_user ON recipe_checks
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- A check's pictures of the project's address, sent by its worker, since a check's key only
-- fetches and pushes them nowhere: each PNG of at most 5 MB under its name, served to the owner's
-- page alone.
CREATE TABLE recipe_check_pictures (
    run_id uuid NOT NULL,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    name text NOT NULL CHECK (name ~ '^[a-z0-9]+(-[a-z0-9]+)*-(desktop|phone)$' AND length(name) <= 68),
    png bytea NOT NULL CHECK (octet_length(png) BETWEEN 8 AND 5242880),
    PRIMARY KEY (run_id, name),
    FOREIGN KEY (run_id) REFERENCES recipe_checks (run_id) ON DELETE CASCADE,
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE recipe_check_pictures ENABLE ROW LEVEL SECURITY;
ALTER TABLE recipe_check_pictures FORCE ROW LEVEL SECURITY;
CREATE POLICY recipe_check_pictures_user ON recipe_check_pictures
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
