-- The end of a feature: once every card of a feature is done, the default branch is brought into
-- the feature's branch as the app, a merge commit, and the feature's pull request to the default
-- branch is opened. Why it is built so is "The end of a feature" in
-- devflow/docs/devflow-architecture.md, and the columns are "The end of a feature" in
-- devflow/README.md.

-- A merge Dev flow made that merged no pull request, the default branch brought into a feature's
-- branch, is recorded as a merge of a pull request is, by its commit and its branch, so the watch
-- finds it fine; its number is null then.
ALTER TABLE merges_made ALTER COLUMN number DROP NOT NULL;

-- The default branch's commit, as its watch last named it, at which bringing it in last failed, a
-- conflict or a refusal; null otherwise. Bringing in is not tried again while the default branch is
-- still at that commit. brought_in_commit, the one last brought in, is 0024's.
ALTER TABLE features ADD COLUMN bring_in_failed_at text CHECK (bring_in_failed_at ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');
