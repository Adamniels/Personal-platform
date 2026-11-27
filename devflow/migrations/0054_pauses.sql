-- Pausing: a project paused by its owner, and every project of a user paused with one switch. While
-- either holds, no job of the project is handed out, of any kind, the setup's and the recipe's
-- check among them; a run under way finishes, and reading GitHub, opening and marking go on. What a
-- pause holds is "Jobs" in devflow/README.md, and why it is RunDecision's first reason "Handing out
-- work" in devflow/docs/devflow-architecture.md.

-- When the owner paused the project, null while it runs. Pausing it again keeps the time.
ALTER TABLE projects ADD COLUMN paused_at timestamptz;

-- One row while every project of the user is paused, laid over each project's own pause: continuing
-- everything removes the row and leaves a project paused on its own still paused.
CREATE TABLE all_paused (
    user_id uuid PRIMARY KEY,
    paused_at timestamptz NOT NULL
);

ALTER TABLE all_paused ENABLE ROW LEVEL SECURITY;
ALTER TABLE all_paused FORCE ROW LEVEL SECURITY;
CREATE POLICY all_paused_user ON all_paused
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
