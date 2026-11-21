-- Sending a card back: a card of a feature that has landed in the feature's branch is sent back by
-- the owner's comment on its issue, posted from the feature's page or written on GitHub; the next
-- read opens it again with his comment as its input, the app opens its issue again, and a fresh
-- agent builds it on a new branch from the feature's branch. The columns are "Sending a card back"
-- in devflow/README.md, and why a sent back card gets a new branch is "Sending a card back" in
-- devflow/docs/devflow-architecture.md.

-- How many times the card was sent back; its branch is <number>-<slug>-<k>, k this plus one, once
-- it was, so a branch that already merged is never used again.
ALTER TABLE cards ADD COLUMN sent_backs int NOT NULL DEFAULT 0 CHECK (sent_backs >= 0);

-- The pull request it had landed in when it was last sent back, which its next brief names; both
-- or neither, and set exactly when it was sent back.
ALTER TABLE cards ADD COLUMN sent_back_from int CHECK (sent_back_from > 0);
ALTER TABLE cards ADD COLUMN sent_back_from_url text;
ALTER TABLE cards ADD CHECK ((sent_back_from IS NULL) = (sent_back_from_url IS NULL));
ALTER TABLE cards ADD CHECK ((sent_backs = 0) = (sent_back_from IS NULL));

-- The owner's comments that last sent it back, each exactly as he wrote it, which its builds quote
-- as his input; null before it was sent back.
ALTER TABLE cards ADD COLUMN sent_back_comments jsonb
    CHECK (sent_back_comments IS NULL OR jsonb_typeof(sent_back_comments) = 'array');

-- The send back, by its count, whose issue the app opened again; null before any. The app opens it
-- while this is behind sent_backs, so a lost answer opens it once more and nothing twice.
ALTER TABLE cards ADD COLUMN reopened_for int CHECK (reopened_for > 0);
ALTER TABLE cards ADD CHECK (reopened_for <= sent_backs);
