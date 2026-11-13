-- `brain import` writes the memory file's tables in a fixed order inside one
-- transaction, events before items, and the two name each other: an item
-- the event that made it, created_by_event, and an event about an item that
-- item, subject_item. No order satisfies both, so one reference has to wait
-- for the end of the transaction. This one may now, see "The memory file" in
-- docs/data-model.md.
--
-- Initially immediate, so every write is checked at once as before; only a
-- transaction that asks with SET CONSTRAINTS, the import's alone, defers it,
-- and Postgres still checks it before that transaction commits.
ALTER TABLE events
    ALTER CONSTRAINT events_user_id_subject_item_fkey DEFERRABLE INITIALLY IMMEDIATE;
