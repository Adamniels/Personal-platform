-- A run's log: the lines its worker posts while it runs, and when Dev flow last heard from it. How a
-- line is posted, checked and kept is "Jobs" in devflow/README.md, and why a run is lost by its
-- heartbeat rather than its time limit "Handing out work" in devflow/docs/devflow-architecture.md.

-- When the run's worker last posted to its log, with lines or none; null before its first post.
-- A run under way whose last_heard_at, or started_at when it has none, is 2 minutes ago is lost.
ALTER TABLE runs ADD COLUMN last_heard_at timestamptz;

-- So a line can name its run together with the run's project and user, and never a run of another
-- user's.
ALTER TABLE runs ADD CONSTRAINT runs_id_project_user UNIQUE (id, project_id, user_id);

-- The runs under way, by when they were last heard from, which the loop looks through for lost
-- ones; the index of 0012 by when they started goes with the rule it served.
DROP INDEX runs_under_way;
CREATE INDEX runs_under_way ON runs (project_id, (coalesce(last_heard_at, started_at))) WHERE finished_at IS NULL;

-- One line of a run's log, by its place in it. A retried post never doubles a line, since a seq
-- already held is skipped, and gaps are allowed. At most 20,000 a run and one system line saying
-- the rest was not kept, which src/DevFlow/Jobs/RunLogStore.cs keeps to. The text is scrubbed of
-- the project's values before it is stored, and cut, so the checks repeat the route's own rules
-- for a row written around them.
CREATE TABLE run_log_lines (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    run_id uuid NOT NULL,
    seq bigint NOT NULL CHECK (seq > 0),
    -- When the worker wrote it, by the worker's clock.
    at timestamptz NOT NULL,
    kind text NOT NULL CHECK (kind IN ('text', 'tool', 'tool_result', 'check', 'system')),
    text text NOT NULL CHECK (text <> '' AND length(text) <= 4000),
    PRIMARY KEY (run_id, seq),
    -- Gone with its run, and so with its project.
    FOREIGN KEY (run_id, project_id, user_id) REFERENCES runs (id, project_id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE run_log_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE run_log_lines FORCE ROW LEVEL SECURITY;
CREATE POLICY run_log_lines_user ON run_log_lines
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- A line is written once and never changed: the app inserts, reads and, for retention, deletes.
REVOKE UPDATE ON run_log_lines FROM devflow_app;
