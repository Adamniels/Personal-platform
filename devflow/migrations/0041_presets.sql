-- The presets: a new project starts as "Only I merge", questions on and waits_for_owner always, and
-- every project there is becomes the same. The presets themselves are never stored, but answered by
-- the settings route from the settings they cover; see "Settings" in devflow/README.md, and why
-- they are not stored "Projects" in devflow/docs/devflow-architecture.md.

-- A new project's. Questions are on by default already, from 0040_questions.sql.
ALTER TABLE project_settings ALTER COLUMN waits_for_owner SET DEFAULT 'always';

-- Every project there is, whatever it had, so each matches "Only I merge" exactly. The conditions
-- of sometimes are kept: on always they are not read, and stay as the owner left them for when he
-- chooses sometimes again. The rows are every user's, which the policy hides even from the owner
-- while it is forced; it is lifted for this one statement and forced again before the transaction
-- ends, as 0025_feature_watches.sql does.
ALTER TABLE project_settings NO FORCE ROW LEVEL SECURITY;
UPDATE project_settings SET questions = true, waits_for_owner = 'always';
ALTER TABLE project_settings FORCE ROW LEVEL SECURITY;
