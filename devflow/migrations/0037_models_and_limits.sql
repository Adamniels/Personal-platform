-- Models and limits: the model that reads, builds and reviews a project's cards, and the time and
-- turns a run may take and the fix rounds a card and a feature get, each a setting with today's
-- value as its default. Every job carries the model and the limits it was handed out with, so a
-- change takes effect at the next decision. The route that changes them is "Settings" in
-- devflow/README.md, and how a job carries them "Jobs" there.

-- Claude Code's aliases, which follow the newest model of each family, so a new model needs no
-- change here. Reading is read by the reading job; building by a build, a fix, a conflict and a
-- feature's fix; reviewing by a card's review and a feature's.
ALTER TABLE project_settings
    ADD COLUMN model_reading text NOT NULL DEFAULT 'opus' CHECK (model_reading IN ('opus', 'sonnet', 'haiku')),
    ADD COLUMN model_building text NOT NULL DEFAULT 'opus' CHECK (model_building IN ('opus', 'sonnet', 'haiku')),
    ADD COLUMN model_reviewing text NOT NULL DEFAULT 'opus' CHECK (model_reviewing IN ('opus', 'sonnet', 'haiku'));

-- The limits, with the ranges the route holds them to.
ALTER TABLE project_settings
    ADD COLUMN run_minutes int NOT NULL DEFAULT 90 CHECK (run_minutes BETWEEN 10 AND 240),
    ADD COLUMN run_turns int NOT NULL DEFAULT 200 CHECK (run_turns BETWEEN 20 AND 1000),
    ADD COLUMN fix_rounds_card int NOT NULL DEFAULT 3 CHECK (fix_rounds_card BETWEEN 0 AND 10),
    ADD COLUMN fix_rounds_feature int NOT NULL DEFAULT 3 CHECK (fix_rounds_feature BETWEEN 0 AND 10);

-- A card and a feature may now go through up to 10 rounds, so what records a round takes up to 10,
-- where it took the fixed 3 before.
ALTER TABLE runs DROP CONSTRAINT runs_fix_round_check;
ALTER TABLE runs ADD CONSTRAINT runs_fix_round_check CHECK (fix_round BETWEEN 1 AND 10);
ALTER TABLE cards DROP CONSTRAINT cards_fix_rounds_check;
ALTER TABLE cards ADD CONSTRAINT cards_fix_rounds_check CHECK (fix_rounds BETWEEN 0 AND 10);
ALTER TABLE cards DROP CONSTRAINT cards_fix_round_check;
ALTER TABLE cards ADD CONSTRAINT cards_fix_round_check CHECK (fix_round BETWEEN 1 AND 10);
ALTER TABLE features DROP CONSTRAINT features_fix_round_check;
ALTER TABLE features ADD CONSTRAINT features_fix_round_check CHECK (fix_round BETWEEN 0 AND 10);
ALTER TABLE features DROP CONSTRAINT features_next_fix_round_check;
ALTER TABLE features ADD CONSTRAINT features_next_fix_round_check CHECK (next_fix_round BETWEEN 1 AND 10);
