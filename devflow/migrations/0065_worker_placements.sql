-- Where a project's workers run: on which of its user's machines, and how many on each, as the
-- Workers card places them, and the slots that follow from that, the place of one worker each. The
-- page says what should exist, and the machine program, in a later slice, makes its machine match
-- it. See "Workers" in devflow/README.md, and in devflow/docs/devflow-architecture.md for why it is
-- built so.

-- A project placed on a machine: how many of its workers that machine should run, 0 to 8, and
-- since when it has had at least one, which decides the first placed machine, the one the setup
-- slot is on. PUT /devflow/api/projects/{id}/placements replaces a project's whole list. Both
-- references carry the user, so a placement can only join a project and a machine of the same
-- user, and removing either removes it.
CREATE TABLE worker_placements (
    project_id uuid NOT NULL,
    machine_id uuid NOT NULL,
    user_id uuid NOT NULL,
    -- The check repeats the route's range, in src/DevFlow/Placements/PlacementEndpoints.cs, for a
    -- row written around the route.
    workers int NOT NULL CHECK (workers BETWEEN 0 AND 8),
    placed_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, machine_id),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE,
    FOREIGN KEY (machine_id, user_id) REFERENCES machines (id, user_id) ON DELETE CASCADE
);

ALTER TABLE worker_placements ENABLE ROW LEVEL SECURITY;
ALTER TABLE worker_placements FORCE ROW LEVEL SECURITY;
CREATE POLICY worker_placements_user ON worker_placements
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- The place of one worker of a project on a machine: a worker slot, numbered from 1 on each
-- machine, or the setup slot, number 1. A placement of N wants worker slots 1 to N. A slot no
-- longer wanted is kept, wanted false, until its machine reports it gone, so a card under way on it
-- is never cut off, and is wanted again when its number is. WorkerSlots, in
-- src/DevFlow/Placements/, brings them up to date in the same transaction as every change that
-- moves what is wanted. Removing the machine removes its slots, since nothing is left to report
-- them.
CREATE TABLE worker_slots (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    machine_id uuid NOT NULL,
    user_id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('worker', 'setup')),
    number int NOT NULL CHECK (number BETWEEN 1 AND 8),
    wanted boolean NOT NULL,
    UNIQUE (project_id, machine_id, kind, number),
    -- A project has one setup slot on a machine, and wants it on one machine at most.
    CHECK (kind = 'worker' OR number = 1),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE,
    FOREIGN KEY (machine_id, user_id) REFERENCES machines (id, user_id) ON DELETE CASCADE
);

ALTER TABLE worker_slots ENABLE ROW LEVEL SECURITY;
ALTER TABLE worker_slots FORCE ROW LEVEL SECURITY;
CREATE POLICY worker_slots_user ON worker_slots
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
