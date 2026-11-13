-- Marking two items as contradicting, and taking it back. See "Conflicting
-- decisions before consolidation" in docs/event-model.md.
--
-- A contradiction is an item relation, which needs the event that made it:
-- the owner's judgment that the two disagree, and of neither that it is
-- wrong, so neutral, and never evidence on either item.
INSERT INTO verbs (verb, kind, strength, polarity) VALUES
    ('contradicted', 'judgment', 3, 0);

-- Taken back, a relation is ended, never deleted, as a topic relation is:
-- the app role may set when it ended and nothing else. 0004 revoked every
-- other change to the item tables.
GRANT UPDATE (ended_at) ON item_relations TO brain_app;
