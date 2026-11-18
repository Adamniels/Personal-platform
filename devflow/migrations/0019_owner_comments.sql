-- The owner's comments on a card's open pull request, the one input Dev flow reads from GitHub: a
-- comment by his linked login starts a fix with it. How a comment is read and taken is "Fixing" in
-- devflow/README.md, and why it is the only one "Fixing" in devflow/docs/devflow-architecture.md.

-- The comments the card's next fix answers, each exactly as he wrote it, set as the card moves to
-- fixing and copied onto the fix's run when it is handed out; null when none.
ALTER TABLE cards ADD COLUMN fix_comments jsonb CHECK (fix_comments IS NULL OR jsonb_typeof(fix_comments) = 'array');

-- The newest of his comments on the card's pull request already taken, by GitHub's id, which only
-- grows, so none is taken twice and none written while a run was under way is lost. Null before
-- any was taken, when every one of his counts.
ALTER TABLE cards ADD COLUMN comments_read_through bigint;

-- The owner's comments a fix answered, as the card held them when it was handed out; null for a
-- run that answered none.
ALTER TABLE runs ADD COLUMN owner_comments jsonb CHECK (owner_comments IS NULL OR jsonb_typeof(owner_comments) = 'array');
