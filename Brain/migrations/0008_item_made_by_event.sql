-- The item an event made, for the timeline, which links an entry to it. See
-- "Events" in README.md.
--
-- The event that makes an item comes first, so it cannot name the item as
-- its subject; the item names it back in created_by_event. Every event that
-- makes an item makes exactly one, a fresh event keyed by the new item's id,
-- so the index is unique: it holds the rule the timeline reads it by, as well
-- as finding the item quickly.
CREATE UNIQUE INDEX items_made_by ON items (user_id, created_by_event);
