-- "Sometimes": a card's pull request waits for the owner only when a condition he ticked holds, the
-- card marked for his review with the review label, the change touching a path on his list, or the
-- agent marking a decision for him. The merge check that reads them is "The merge check" in
-- devflow/docs/devflow-architecture.md, and the route that changes them "Settings" in
-- devflow/README.md.

-- The third value, between the two.
ALTER TABLE project_settings DROP CONSTRAINT project_settings_waits_for_owner_check;
ALTER TABLE project_settings ADD CONSTRAINT project_settings_waits_for_owner_check
    CHECK (waits_for_owner IN ('never', 'sometimes', 'always'));

-- The three conditions, all ticked by default and read only on sometimes, where at least one is
-- ticked, since sometimes with none waits for nothing and would be never under another name.
ALTER TABLE project_settings
    ADD COLUMN wait_when_marked boolean NOT NULL DEFAULT true,
    ADD COLUMN wait_when_paths boolean NOT NULL DEFAULT true,
    ADD COLUMN wait_when_decision boolean NOT NULL DEFAULT true,
    ADD CONSTRAINT project_settings_sometimes_check
        CHECK (waits_for_owner <> 'sometimes' OR wait_when_marked OR wait_when_paths OR wait_when_decision);

-- The label that marks a card, or a feature's parent and so every card of it, for the owner's
-- review: a fourth label, held to the rules of the other three, a different name from each whatever
-- the case.
ALTER TABLE project_settings
    ADD COLUMN review_label text NOT NULL DEFAULT 'devflow-review' CHECK (review_label <> '' AND length(review_label) <= 50);

-- A project that already named one of its labels devflow-review keeps it, and its review label gets
-- a name of its own, so the rule below holds for every row.
UPDATE project_settings SET review_label = 'devflow-review-1'
WHERE lower(review_label) IN (lower(pickup_label), lower(hold_label), lower(attention_label));

ALTER TABLE project_settings ADD CONSTRAINT project_settings_review_label_differs
    CHECK (lower(review_label) <> lower(pickup_label)
        AND lower(review_label) <> lower(hold_label)
        AND lower(review_label) <> lower(attention_label));

-- The paths the owner wants to see, glob patterns in the order he gave them, none by default. A
-- pattern's rules, at most 200 characters among them, and at most 100 patterns, are the route's,
-- which names the field, as a login's are; the table keeps only that the list is a flat list with no
-- null in it.
ALTER TABLE project_settings ADD COLUMN wait_paths text[] NOT NULL DEFAULT '{}'
    CHECK (array_ndims(wait_paths) IS NULL OR (array_ndims(wait_paths) = 1 AND array_position(wait_paths, NULL) IS NULL));
