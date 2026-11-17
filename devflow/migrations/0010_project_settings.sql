-- A project's settings, a row per project, made with the project by the trigger below. Only the
-- settings something reads: the labels picking cards reads and whether a card's pull request
-- waits for the owner. The design's other settings arrive with the parts that read them, each in
-- its own migration, a deliberate exception to "a table arrives in its documented final shape",
-- see "Cards" in devflow/docs/devflow-architecture.md. The routes are "Cards" in
-- devflow/README.md.

-- A label is at most 50 characters, the most GitHub takes, and the three are different names,
-- whatever their case, since GitHub's label names are. The rest of a label's rules, no comma and
-- no space at either end, are the route's, which names the field.
CREATE TABLE project_settings (
    project_id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- A card is an open issue carrying it.
    pickup_label text NOT NULL DEFAULT 'devflow' CHECK (pickup_label <> '' AND length(pickup_label) <= 50),
    -- A card carrying it is held.
    hold_label text NOT NULL DEFAULT 'devflow-hold' CHECK (hold_label <> '' AND length(hold_label) <= 50),
    -- What a card or an issue that needs the owner is marked with, by a later part.
    attention_label text NOT NULL DEFAULT 'devflow-attention' CHECK (attention_label <> '' AND length(attention_label) <= 50),
    -- Whether a card's pull request waits for the owner, read by a later part.
    waits_for_owner text NOT NULL DEFAULT 'never' CHECK (waits_for_owner IN ('never', 'always')),
    CHECK (lower(pickup_label) <> lower(hold_label)
        AND lower(pickup_label) <> lower(attention_label)
        AND lower(hold_label) <> lower(attention_label)),
    -- Follows the project, gone with it and renumbered with it, as poll_targets is.
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE project_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY project_settings_user ON project_settings
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- The defaults for every project connected before this migration.
INSERT INTO project_settings (project_id, user_id) SELECT id, user_id FROM projects;

-- Makes a project's settings with the project, so no code path can forget. It runs as the caller,
-- not as the owner as sync_poll_target does: the app role inserting a project through the door
-- may write its settings under the same policy, so nothing more is needed.
CREATE FUNCTION make_project_settings() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = public, pg_temp
AS $$
BEGIN
    INSERT INTO public.project_settings (project_id, user_id) VALUES (NEW.id, NEW.user_id);
    RETURN NULL;
END;
$$;

CREATE TRIGGER projects_settings
    AFTER INSERT ON projects
    FOR EACH ROW EXECUTE FUNCTION make_project_settings();
