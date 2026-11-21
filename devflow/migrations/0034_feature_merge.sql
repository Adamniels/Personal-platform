-- Merging a feature into the default branch, as the owner with his own token, and what follows it:
-- the feature is offered for merging only while CI passed on its head; merging confirms each
-- decision he had not answered; and once its pull request merged or closed, its cards are told how
-- it ended, as the app. How it is done is "Merging a feature" in devflow/README.md, and why it is
-- built so "Merging a feature" in devflow/docs/devflow-architecture.md.

-- The feature branch's head at which the minute last read CI as passed, read as the merge check
-- reads it; null when it has not, or could not read it. The merge is offered only while it is the
-- head the branch's watch names, so the page and the route refuse a head CI has not passed on
-- without asking the code host.
ALTER TABLE features ADD COLUMN ci_passed_commit text CHECK (ci_passed_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');

-- When the feature's cards were told how it ended, once it is merged or closed: the cards still open
-- closed by the app after a merge, or the cards closed opened again after a close. Null until then,
-- and for every other feature.
ALTER TABLE features ADD COLUMN outcome_told_at timestamptz;

-- An answer is also merged, a decision confirmed by merging the feature it belongs to, as merging a
-- pull request on GitHub confirms the decisions in it.
ALTER TABLE decision_answers DROP CONSTRAINT decision_answers_answer_check;
ALTER TABLE decision_answers ADD CONSTRAINT decision_answers_answer_check CHECK (answer IN ('fine', 'sent_back', 'merged'));
