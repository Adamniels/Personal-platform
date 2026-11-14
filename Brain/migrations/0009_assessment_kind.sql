-- A seventh kind of event, assessment: the owner was tested on what he knows,
-- and its first two verbs. Why it is a kind of its own is "Seven kinds of
-- event" in docs/event-model.md, and the rows are "The verb list" in
-- docs/data-model.md.

-- The kinds stay a closed list, so widening it is this migration. The check
-- is the one 0003 declared inline on the column, under the name Postgres
-- gave it, and it keeps that name.
ALTER TABLE verbs DROP CONSTRAINT verbs_kind_check;
ALTER TABLE verbs ADD CONSTRAINT verbs_kind_check CHECK (
    kind IN (
        'exposure', 'engagement', 'judgment', 'assertion', 'decision', 'activity',
        'assessment'
    )
);

-- Self graded, so medium evidence: a review grade is the owner's own.
INSERT INTO verbs (verb, kind, strength, polarity) VALUES
    ('recalled', 'assessment', 2, 1),
    ('forgot', 'assessment', 2, -1);
