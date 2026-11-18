-- Watching each project's default branch: where the watch stands, the merges Dev flow made itself,
-- a project stopped by something that landed that Dev flow did not put there, and each time its user
-- resumed one. Why it is built so is "Watching the default branch" in
-- devflow/docs/devflow-architecture.md, and the tables and the route are the section of that name in
-- devflow/README.md. Each table follows its project, gone with it and renumbered with it, and is
-- guarded like every scoped table.

-- Where a project's watch stands: the branch watched, the newest change it has judged, and when it
-- last read the branch's changes. A project without a row, or whose last read is more than five
-- minutes old, hands out no new work.
CREATE TABLE branch_watches (
    project_id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- The repository's default branch as the code host said it when the watch began on it.
    branch text NOT NULL CHECK (branch <> ''),
    -- The code host's id of the newest change judged, or that it began at; null when the branch had
    -- none when the watch began, so every change after is judged.
    last_activity_id bigint,
    began_at timestamptz NOT NULL,
    -- When its changes were last read and judged.
    last_read_at timestamptz NOT NULL,
    last_attempt_at timestamptz NOT NULL,
    -- Why the last read failed, a short fixed phrase and never the code host's own text; null after
    -- a success.
    last_error text CHECK (last_error <> ''),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE branch_watches ENABLE ROW LEVEL SECURITY;
ALTER TABLE branch_watches FORCE ROW LEVEL SECURITY;
CREATE POLICY branch_watches_user ON branch_watches
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- A merge Dev flow made as the app, by its merge commit, written in the transaction that records
-- the merge, so the watch finds it fine when it lands.
CREATE TABLE merges_made (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    commit text NOT NULL CHECK (commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    -- The pull request it merged.
    number int NOT NULL CHECK (number > 0),
    merged_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, commit),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE merges_made ENABLE ROW LEVEL SECURITY;
ALTER TABLE merges_made FORCE ROW LEVEL SECURITY;
CREATE POLICY merges_made_user ON merges_made
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- A stopped project, a row while it is stopped: when it stopped, and the newest change that
-- stopped it, which is what its user looks at and accepts by resuming.
CREATE TABLE project_stops (
    project_id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- When it first stopped; a later change that stops it again changes what it shows, not this.
    stopped_at timestamptz NOT NULL,
    branch text NOT NULL CHECK (branch <> ''),
    activity_id bigint NOT NULL,
    activity_type text NOT NULL CHECK (activity_type IN ('push', 'force_push', 'branch_creation', 'branch_deletion',
                                                         'pull_request_merge', 'merge_queue_merge')),
    -- The login that made it, data; null when the code host names none.
    actor text CHECK (actor <> ''),
    -- The commit the branch was at after it.
    commit text NOT NULL CHECK (commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    landed_at timestamptz NOT NULL,
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE project_stops ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_stops FORCE ROW LEVEL SECURITY;
CREATE POLICY project_stops_user ON project_stops
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- Each resume, kept, so a later reader sees who accepted what and when: the stop as it was, who
-- resumed it and the commit accepted.
CREATE TABLE project_resumes (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    resumed_by uuid NOT NULL,
    resumed_at timestamptz NOT NULL,
    accepted_commit text NOT NULL CHECK (accepted_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    branch text NOT NULL CHECK (branch <> ''),
    activity_type text NOT NULL CHECK (activity_type IN ('push', 'force_push', 'branch_creation', 'branch_deletion',
                                                         'pull_request_merge', 'merge_queue_merge')),
    actor text CHECK (actor <> ''),
    stopped_at timestamptz NOT NULL,
    landed_at timestamptz NOT NULL,
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE project_resumes ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_resumes FORCE ROW LEVEL SECURITY;
CREATE POLICY project_resumes_user ON project_resumes
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
