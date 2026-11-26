-- Dropping a card the owner stopped: Dev flow takes its pick-up label off as him and leaves it
-- ignored, "Dropped by you", its branch and any pull request as they were. How it is done is
-- "Stops" in devflow/README.md.

-- When the owner dropped the card, null for every card he has not. A read of the project keeps a
-- dropped card ignored while its label is off, where a card not found any more is otherwise
-- removed, and a read that began after the drop and finds it labelled again picks it afresh, as any
-- card. Only an ignored card is dropped.
ALTER TABLE cards ADD COLUMN dropped_at timestamptz;
ALTER TABLE cards ADD CONSTRAINT cards_dropped_is_ignored CHECK (dropped_at IS NULL OR state = 'ignored');
