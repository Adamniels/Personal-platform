-- A card of a feature lands in the feature's branch, and its issue is closed by the app with a
-- comment saying where it landed, since GitHub's closing keywords close an issue only from a pull
-- request into the default branch. Why it is built so is "A card of a feature" under "The merge
-- check" in devflow/docs/devflow-architecture.md, and the column is "The merge check" in
-- devflow/README.md.

-- The pull request whose landing the card's issue was told of, commented on and closed; null until
-- then. A number rather than a flag, so a card sent back and landing again with another pull
-- request is told again without anything clearing it.
ALTER TABLE cards ADD COLUMN landing_told_for int CHECK (landing_told_for > 0);
