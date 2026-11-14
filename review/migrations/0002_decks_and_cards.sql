-- Decks and cards, in their final shape. What a card is, and each rule the
-- checks below repeat, is "What a card is" in review/README.md.
--
-- Each table is scoped to a user: user_id, row level security enabled and
-- forced, and the door's one policy, the expression ReviewDb.Guard holds. No
-- id has a default: review mints every one, a UUIDv7, and passes every time
-- in, from its injected clock.
--
-- Every reference between rows carries user_id, so it can only name a row of
-- the same user. A foreign key is checked without row level security, so one
-- on id alone would accept another user's deck. The brain's tables do the
-- same.
--
-- Lengths are characters, as char_length counts them; review checks the same
-- rules first, to answer 422 naming the field.

-- Where a card lives, to choose what to study: review's own organisation.
CREATE TABLE decks (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- Stored trimmed.
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 100),
    created_at timestamptz NOT NULL,
    -- What cards point at.
    UNIQUE (user_id, id)
);

-- One deck of a name per user, in any letter case.
CREATE UNIQUE INDEX decks_name ON decks (user_id, lower(name));

ALTER TABLE decks ENABLE ROW LEVEL SECURITY;
ALTER TABLE decks FORCE ROW LEVEL SECURITY;
CREATE POLICY decks_user ON decks
    USING (user_id = nullif(current_setting('review.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('review.user_id', true), '')::uuid);

-- A front and a back, plain text, in one deck, with an optional source, the
-- address and title of another feature's record at the time, and its
-- schedule: FSRS-6's card state as py-fsrs 6.3.3's Card holds it, which
-- grading fills. A change to a card never touches the schedule.
CREATE TABLE cards (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- A deck holding cards cannot be deleted.
    deck_id uuid NOT NULL,
    -- Stored trimmed, with the line breaks inside kept.
    front text NOT NULL CHECK (char_length(front) BETWEEN 1 AND 4000),
    back text NOT NULL CHECK (char_length(back) BETWEEN 1 AND 4000),
    -- A path on the platform, never a URL, by the core's rule for a return
    -- path: it starts with one slash, and holds no backslash, control
    -- character or whitespace, since a browser reads \ as / and drops tabs
    -- and newlines, which turns /\evil and /<tab>/evil into //evil, another
    -- host. The class lists them by code point, C#'s char.IsControl and
    -- char.IsWhiteSpace, so it does not depend on the database's locale.
    source_path text NULL CHECK (
        char_length(source_path) <= 500
        AND left(source_path, 1) = '/'
        AND left(source_path, 2) <> '//'
        AND source_path !~ '[\\\u0001- \u007F-\u009F   -     　]'
    ),
    source_title text NULL CHECK (char_length(source_title) BETWEEN 1 AND 200),
    state text NOT NULL DEFAULT 'new' CHECK (state IN ('new', 'learning', 'review', 'relearning')),
    step integer NULL,
    stability double precision NULL,
    difficulty double precision NULL,
    -- When it is next due; a new card is due when it was made.
    due timestamptz NOT NULL,
    last_review timestamptz NULL,
    -- Moves on with every change, so a change from a stale copy is refused.
    version integer NOT NULL DEFAULT 1,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    -- What its topics point at.
    UNIQUE (user_id, id),
    FOREIGN KEY (user_id, deck_id) REFERENCES decks (user_id, id) ON DELETE RESTRICT,
    CHECK ((source_path IS NULL) = (source_title IS NULL))
);

-- The queue of due cards reads by this.
CREATE INDEX cards_due ON cards (user_id, due);
-- A deck's cards: counting them, narrowing the list, and the check that
-- refuses deleting a deck that holds any.
CREATE INDEX cards_deck ON cards (user_id, deck_id);

ALTER TABLE cards ENABLE ROW LEVEL SECURITY;
ALTER TABLE cards FORCE ROW LEVEL SECURITY;
CREATE POLICY cards_user ON cards
    USING (user_id = nullif(current_setting('review.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('review.user_id', true), '')::uuid);

-- What a card is about: one to five of the brain's topics, each by its slug,
-- with the brain's name for it when the card was saved, a snapshot. Review
-- keeps no topics of its own and does not ask the brain whether one exists.
CREATE TABLE card_topics (
    card_id uuid NOT NULL,
    user_id uuid NOT NULL,
    -- Its place in the card's list, from 0.
    position smallint NOT NULL CHECK (position BETWEEN 0 AND 4),
    -- The brain's slug rule.
    slug text NOT NULL CHECK (slug ~ '^[a-z0-9][a-z0-9-]{0,79}$'),
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 200),
    PRIMARY KEY (card_id, slug),
    UNIQUE (card_id, position),
    FOREIGN KEY (user_id, card_id) REFERENCES cards (user_id, id) ON DELETE CASCADE
);

-- Narrowing the list by a topic.
CREATE INDEX card_topics_slug ON card_topics (user_id, slug);

ALTER TABLE card_topics ENABLE ROW LEVEL SECURITY;
ALTER TABLE card_topics FORCE ROW LEVEL SECURITY;
CREATE POLICY card_topics_user ON card_topics
    USING (user_id = nullif(current_setting('review.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('review.user_id', true), '')::uuid);
