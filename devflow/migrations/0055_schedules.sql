-- When a project runs, and the user's time zone it is read in. A project runs always, the default,
-- or by schedule: inside any of its windows that is on, read as wall clock in its user's zone, and
-- with none on, never. Outside, no job of it is handed out, of any kind; a run under way finishes.
-- The rules a window keeps, and how inside and the next start are worked out, are "Schedules" in
-- devflow/README.md; why the zone is the user's and why a window is wall clock, "Projects" in
-- devflow/docs/devflow-architecture.md.

-- One row per user who set a zone; a user without one is in Europe/Stockholm. Read and written only
-- through DevFlowTime in src/DevFlow/DevFlowTime.cs, which checks the id is an IANA zone .NET knows;
-- the check here only bounds it.
CREATE TABLE user_settings (
    user_id uuid PRIMARY KEY,
    time_zone text NOT NULL DEFAULT 'Europe/Stockholm' CHECK (time_zone <> '' AND length(time_zone) <= 64)
);

ALTER TABLE user_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY user_settings_user ON user_settings
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- Whether the project runs always or by its schedules.
ALTER TABLE project_settings ADD COLUMN runs_by text NOT NULL DEFAULT 'always' CHECK (runs_by IN ('always', 'schedule'));

-- A project's windows, replaced whole by its route. The checks repeat the route's own rules, in
-- src/DevFlow/Schedules/ScheduleRules.cs, for a row written around them.
CREATE TABLE project_schedules (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    id uuid NOT NULL,
    name text NOT NULL CHECK (name = btrim(name) AND length(name) BETWEEN 1 AND 60),
    -- The days it starts on, in the week's order from Monday, each once.
    days text[] NOT NULL CHECK (cardinality(days) BETWEEN 1 AND 7 AND days <@ ARRAY['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun']),
    -- Wall clock in the user's zone, to the minute; a to earlier than from crosses midnight.
    from_time time NOT NULL CHECK (date_trunc('minute', from_time) = from_time),
    to_time time NOT NULL CHECK (date_trunc('minute', to_time) = to_time AND to_time <> from_time),
    active boolean NOT NULL,
    -- Its place in the list, from 0; at most 20 windows.
    position int NOT NULL CHECK (position BETWEEN 0 AND 19),
    PRIMARY KEY (project_id, id),
    UNIQUE (project_id, position),
    -- Follows the project, gone with it and renumbered with it, as project_settings is.
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

-- A name is unique in its project whatever its case.
CREATE UNIQUE INDEX project_schedules_name ON project_schedules (project_id, lower(name));

ALTER TABLE project_schedules ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_schedules FORCE ROW LEVEL SECURITY;
CREATE POLICY project_schedules_user ON project_schedules
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
