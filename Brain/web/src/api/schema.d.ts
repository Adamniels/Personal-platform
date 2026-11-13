// Generated from contracts/brain/openapi.json by tools/generate-schema.mjs, with
// `npm run generate:api`. Do not edit: src/api/schema.test.ts fails when it is stale.

export interface paths {
    "/brain/api/ask/history": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Ask History
         * @description What is the history of this subject: the records and decisions about
         *     one topic, read through aliases, or one named thing, ordered by mode.
         *     Asked about history, the decisions another one replaced are there too,
         *     inactive and superseded. A subject the user does not have answers known
         *     false, with no items, and is never created. 422 for neither or both of
         *     topic and entity, a malformed slug, or an unknown mode or field.
         */
        post: operations["ask_history_brain_api_ask_history_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/ask/now": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Ask Now
         * @description What is going on now for this person: their active states, ordered
         *     by mode, each flagged stale, asked about now, once it has gone 60 days
         *     unsupported. 422 for an unknown mode or field.
         */
        post: operations["ask_now_brain_api_ask_now_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/ask/profile": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Ask Profile
         * @description Who is this person: their active profile entries, grouped by
         *     category, every one of the six in order, an empty one included, and then
         *     their rules, each group ordered by mode. A topic's focus is in the
         *     topics answer instead. 422 for an unknown mode or field.
         */
        post: operations["ask_profile_brain_api_ask_profile_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/ask/topics": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Ask Topics
         * @description What does this person know about these topics: one entry per slug
         *     asked, in the order asked, each naming the slug asked and the topic that
         *     answered, an alias's target, with its active level and focus, or null,
         *     its corrected misconceptions and its other history entries, items about
         *     its aliases included, each ordered by mode. A slug the user has no topic
         *     by answers known false, with nothing in it, and is never created. 422
         *     for no slug, more than 50, a malformed one, or an unknown mode or
         *     field.
         */
        post: operations["ask_topics_brain_api_ask_topics_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/entities": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * List Entities
         * @description Every named thing the user has, by name.
         */
        get: operations["list_entities_brain_api_entities_get"];
        put?: never;
        /**
         * Create Entity
         * @description A new named thing, with an optional label such as project. A name the
         *     user already has, in any letter case, is 409.
         */
        post: operations["create_entity_brain_api_entities_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/events": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * List Events
         * @description The timeline: the user's events, newest recorded first, 50 at a time
         *     unless limit says otherwise, at most 200. The next page is the one before
         *     the last event's recorded_at.
         */
        get: operations["list_events_brain_api_events_get"];
        put?: never;
        /**
         * Record Event
         * @description Records what happened, 201. The same origin and idempotency key again
         *     is 200, with the event already recorded. 422 for an unknown verb, an
         *     origin of brain, evidence that is missing or blank, a topic and a named
         *     thing together, a malformed slug, or a payload that is not a JSON object
         *     of at most 16 KiB. An unknown topic is created provisional, an unknown
         *     named thing is created too.
         */
        post: operations["record_event_brain_api_events_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/health": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Health
         * @description Public: answers whenever the process serves, and needs no token.
         */
        get: operations["health_brain_api_health_get"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/history": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * List History
         * @description What has happened: the user's history entries and decisions by
         *     occurred_at, oldest first, each with its subjects. Active ones, and each
         *     decision another one replaced, inactive, with replaced_by naming the one
         *     that did; retired entries and older versions are left out. Narrowed by
         *     topic and entity as the item list is. 422 for a malformed slug.
         */
        get: operations["list_history_brain_api_history_get"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/items": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * List Items
         * @description The user's items, newest first, each with its subjects, narrowed by
         *     kind, status, topic and entity when they are given. A topic matches
         *     through aliases, and only itself, not the topics under it. 422 for an
         *     unknown kind or status, or a malformed slug.
         */
        get: operations["list_items_brain_api_items_get"];
        put?: never;
        /**
         * Write Item
         * @description Writes an entry made by hand, 201 with the item, its evidence and its
         *     relations. It applies at once: an asserted event, origin brain and source
         *     manual-entry, is recorded with it and is its supporting evidence. kind is
         *     profile, with a category, or rule, which is global, or state, what is
         *     going on now, with optional subjects like a profile entry; or topic_level or
         *     topic_focus, each on one topic with a value, which where the topic,
         *     read through its alias, has an active entry of that kind already is an
         *     edit of it, 201 with the new version. 422 for an unknown kind, category
         *     or value, content that is blank or over 4000 characters, a rule naming a
         *     subject, a topic the user does not have, or a level or focus the topic
         *     has already; or history, something that happened, with occurred_at and
         *     occurred_precision required and optional subjects, or, with details, a
         *     corrected misconception on one topic, its details misconception true with
         *     what was believed and what is right, and its content built from the two.
         *     Or decision, with details holding its reasoning and what it rejected,
         *     recorded with a decided event in place of an asserted one, and its
         *     occurred_at when it was made, the request's time unless given. A named
         *     thing the user does not have is created.
         */
        post: operations["write_item_brain_api_items_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/items/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Read Item
         * @description One item with its subjects, the events behind it and its relations.
         *     404 for an id the user does not have.
         */
        get: operations["read_item_brain_api_items__id__get"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/items/{id}/confirm": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Confirm Item
         * @description Says a claim still holds, 200 with it. A confirmed event against it,
         *     linked as supporting evidence, is recorded in one transaction, and moves
         *     when it was last supported, so a stale state is stale no longer. Takes
         *     no body. 404 for an id the user does not have. 422 for a record, which
         *     asserts nothing about now. 409 for an item that is not active.
         */
        post: operations["confirm_item_brain_api_items__id__confirm_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/items/{id}/edit": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Edit Item
         * @description Corrects an item, 201 with the new version. Nothing is changed in
         *     place: the new version shares the old one's lineage_id, is active and
         *     asserted, and holds the fields given and the old version's everything
         *     else; the old one becomes inactive, superseded, and a supersedes relation
         *     links the new to it. An edited event against the old version and an
         *     asserted event behind the new one are recorded with it, in one
         *     transaction. kind is the item's own. 404 for an id the user does not
         *     have. 422 for another kind, a field the kind does not take, a topic the
         *     user does not have, or an edit that changes nothing. 409 for a version
         *     that is not the newest, or an item that is retired.
         */
        post: operations["edit_item_brain_api_items__id__edit_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/items/{id}/replace": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Replace Item
         * @description Replaces a decision with a newer one, 200 with the old one, now
         *     inactive with reason superseded and a supersedes relation from the new
         *     one. An edited event against the old one, which makes the relation, is
         *     recorded with it, in one transaction; its decided event stays, and both
         *     stay readable. 404 for an id the user does not have. 422 for a
         *     replaced_by they do not have, either not being a decision, or two
         *     versions of one entry, a decision itself included. 409 when either is
         *     not active.
         */
        post: operations["replace_item_brain_api_items__id__replace_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/items/{id}/restore": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Restore Item
         * @description Brings a retired item back, 200 with it, active again. A restored
         *     event against it is recorded with it, in one transaction. Takes no body.
         *     404 for an id the user does not have. 409 for an item that is not
         *     retired: an active one, or a superseded version, which stays history.
         */
        post: operations["restore_item_brain_api_items__id__restore_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/items/{id}/retire": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Retire Item
         * @description Puts an item away, 200 with it, now inactive with reason retired. It
         *     stops applying, stays readable, and can be restored. A retired event
         *     against it is recorded with it, in one transaction. Takes no body. 404
         *     for an id the user does not have. 409 for an item already retired, or a
         *     superseded version, whose newest version is the one to retire.
         */
        post: operations["retire_item_brain_api_items__id__retire_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/items/{id}/versions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * List Versions
         * @description Every version of the entry the item is one of, oldest first, each
         *     with its subjects, whichever version is asked for. An entry never edited
         *     has one. 404 for an id the user does not have.
         */
        get: operations["list_versions_brain_api_items__id__versions_get"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/knowledge": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * List Knowledge
         * @description What the user knows: one row per topic that is not an alias, by slug,
         *     each alias folded into its target, with its active level and focus, each
         *     with its id and when it was last set, or null, and its count of active
         *     history entries. A topic with neither is listed too. Narrowed by level,
         *     focus and state when they are given. 422 for a value not on their
         *     lists.
         */
        get: operations["list_knowledge_brain_api_knowledge_get"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/now": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * List Now
         * @description What is going on now: the user's active state entries, newest support
         *     first, each with its subjects, last_supported_at, when the newest event
         *     supporting it happened, and stale, true when that is more than 60 days
         *     before the request. Confirming one moves it to the front.
         */
        get: operations["list_now_brain_api_now_get"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/search": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Search
         * @description The user's items holding every word of q, in what an item says, its
         *     content, value, category and the text in its details, or in the evidence
         *     of an event behind it; word forms match, `indexing` finding `indexes`,
         *     but not a word inside another. Narrowed to any of kind, when given, and
         *     to active items unless include_inactive. Newest last_supported_at first,
         *     at most limit, 50 by default, 1 to 200, with no rank: an excerpt shows
         *     where the words are. A query of operators or common words alone finds
         *     nothing. 422 for a q blank or over 200 characters, an unknown kind, a
         *     limit off its range, or an unknown field.
         */
        get: operations["search_brain_api_search_get"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/topic-relations/{id}/end": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * End Relation
         * @description Ends a relation, which stays, with the time it ended. One already
         *     ended is answered as it is.
         */
        post: operations["end_relation_brain_api_topic_relations__id__end_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/topics": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * List Topics
         * @description Every topic the user has, aliases included, by slug.
         */
        get: operations["list_topics_brain_api_topics_get"];
        put?: never;
        /**
         * Create Topic
         * @description A new canonical topic. A slug that is not given is made from the name;
         *     one the user already has is 409.
         */
        post: operations["create_topic_brain_api_topics_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/topics/{id}/broader": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Add Broader
         * @description Places the topic under a broader one, a broader_than relation from the
         *     broader topic to this one. 422 when the broader topic is not there or the
         *     relation would close a loop, read through aliases; 409 when it already
         *     holds.
         */
        post: operations["add_broader_brain_api_topics__id__broader_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/topics/{id}/promote": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Promote Topic
         * @description Makes a provisional topic canonical. An alias is 422.
         */
        post: operations["promote_topic_brain_api_topics__id__promote_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/topics/{id}/same-as": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Same As Topic
         * @description Marks the topic the same as the target, making it an alias that is
         *     read as the target. Nothing that names it changes. 422 when the target
         *     is itself, is not there, is an alias or is provisional, or when the topic
         *     is an alias or has aliases of its own.
         */
        post: operations["same_as_topic_brain_api_topics__id__same_as_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/topics/{id}/separate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /**
         * Separate Topic
         * @description Makes an alias its own topic again. A topic that is not an alias is
         *     answered as it is.
         */
        post: operations["separate_topic_brain_api_topics__id__separate_post"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/brain/api/whoami": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /**
         * Whoami
         * @description The account the request's token names, from the token alone.
         */
        get: operations["whoami_brain_api_whoami_get"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        /**
         * AnswerItem
         * @description An item as every answer gives it, with why it is believed. Nothing
         *     here is stored: support, last_supported_at and stale are computed from
         *     the evidence when the question is asked.
         */
        AnswerItem: {
            /** Category */
            category: string | null;
            /** Content */
            content: string;
            /** Details */
            details: {
                [key: string]: unknown;
            };
            /** Evidence */
            evidence: components["schemas"]["Reason"][];
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Inactive Reason */
            inactive_reason: ("superseded" | "retired") | null;
            /**
             * Kind
             * @enum {string}
             */
            kind: "profile" | "rule" | "topic_level" | "topic_focus" | "state" | "history" | "decision";
            /**
             * Last Supported At
             * Format: date-time
             */
            last_supported_at: string;
            /**
             * Nature
             * @enum {string}
             */
            nature: "claim" | "record";
            /** Occurred At */
            occurred_at: string | null;
            /** Occurred Precision */
            occurred_precision: ("instant" | "day" | "month" | "year") | null;
            /**
             * Provenance
             * @enum {string}
             */
            provenance: "asserted" | "derived";
            /** Stale */
            stale: boolean;
            /**
             * Status
             * @enum {string}
             */
            status: "active" | "inactive";
            /**
             * Store
             * @enum {string}
             */
            store: "profile" | "procedural" | "knowledge" | "semantic" | "reported_history";
            subjects: components["schemas"]["Subjects"];
            support: components["schemas"]["Support"];
            /** Value */
            value: string | null;
        };
        /** Entity */
        Entity: {
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Label */
            label: string | null;
            /** Name */
            name: string;
        };
        /** Event */
        Event: {
            entity: components["schemas"]["EventEntity"] | null;
            /** Evidence */
            evidence: string;
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Idempotency Key */
            idempotency_key: string;
            /**
             * Occurred At
             * Format: date-time
             */
            occurred_at: string;
            /**
             * Occurred Precision
             * @enum {string}
             */
            occurred_precision: "instant" | "day" | "month" | "year";
            /** Origin */
            origin: string;
            /** Payload */
            payload: {
                [key: string]: unknown;
            };
            /** Payload Version */
            payload_version: number;
            /**
             * Recorded At
             * Format: date-time
             */
            recorded_at: string;
            /** Run */
            run: string | null;
            /** Source */
            source: string | null;
            /** Subject Item */
            subject_item: string | null;
            /** Subject Topic */
            subject_topic: string | null;
            topic: components["schemas"]["EventTopic"] | null;
            /** Verb */
            verb: string;
        };
        /** EventEntity */
        EventEntity: {
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Name */
            name: string;
        };
        /** EventTopic */
        EventTopic: {
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Name */
            name: string;
            /** Slug */
            slug: string;
        };
        /** Evidence */
        Evidence: {
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            event: components["schemas"]["EvidenceEvent"];
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /**
             * Kind
             * @enum {string}
             */
            kind: "supports" | "contradicts";
        };
        /**
         * EvidenceEvent
         * @description The event behind a piece of evidence, as much of it as says why.
         */
        EvidenceEvent: {
            /** Evidence */
            evidence: string;
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /**
             * Occurred At
             * Format: date-time
             */
            occurred_at: string;
            /**
             * Occurred Precision
             * @enum {string}
             */
            occurred_precision: "instant" | "day" | "month" | "year";
            /** Origin */
            origin: string;
            /**
             * Recorded At
             * Format: date-time
             */
            recorded_at: string;
            /** Source */
            source: string | null;
            /** Verb */
            verb: string;
        };
        /** Health */
        Health: {
            /**
             * Status
             * @constant
             */
            status: "ok";
        };
        /** HistoryAnswer */
        HistoryAnswer: {
            /** Asked */
            asked: string;
            entity: components["schemas"]["EventEntity"] | null;
            /** Items */
            items: components["schemas"]["AnswerItem"][];
            /** Known */
            known: boolean;
            /**
             * Mode
             * @enum {string}
             */
            mode: "now" | "history";
            topic: components["schemas"]["EventTopic"] | null;
        };
        /**
         * HistoryRow
         * @description A history entry or a decision as the history view reads it.
         */
        HistoryRow: {
            /** Category */
            category: string | null;
            /** Content */
            content: string;
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            /**
             * Created By Event
             * Format: uuid
             */
            created_by_event: string;
            /** Details */
            details: {
                [key: string]: unknown;
            };
            /** Entities */
            entities: components["schemas"]["EventEntity"][];
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Inactive Reason */
            inactive_reason: ("superseded" | "retired") | null;
            /**
             * Kind
             * @enum {string}
             */
            kind: "profile" | "rule" | "topic_level" | "topic_focus" | "state" | "history" | "decision";
            /**
             * Lineage Id
             * Format: uuid
             */
            lineage_id: string;
            /**
             * Nature
             * @enum {string}
             */
            nature: "claim" | "record";
            /** Occurred At */
            occurred_at: string | null;
            /** Occurred Precision */
            occurred_precision: ("instant" | "day" | "month" | "year") | null;
            /**
             * Provenance
             * @enum {string}
             */
            provenance: "asserted" | "derived";
            /** Replaced By */
            replaced_by: string | null;
            /**
             * Status
             * @enum {string}
             */
            status: "active" | "inactive";
            /**
             * Store
             * @enum {string}
             */
            store: "profile" | "procedural" | "knowledge" | "semantic" | "reported_history";
            /** Topics */
            topics: components["schemas"]["EventTopic"][];
            /** Value */
            value: string | null;
        };
        /** Item */
        Item: {
            /** Category */
            category: string | null;
            /** Content */
            content: string;
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            /**
             * Created By Event
             * Format: uuid
             */
            created_by_event: string;
            /** Details */
            details: {
                [key: string]: unknown;
            };
            /** Entities */
            entities: components["schemas"]["EventEntity"][];
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Inactive Reason */
            inactive_reason: ("superseded" | "retired") | null;
            /**
             * Kind
             * @enum {string}
             */
            kind: "profile" | "rule" | "topic_level" | "topic_focus" | "state" | "history" | "decision";
            /**
             * Lineage Id
             * Format: uuid
             */
            lineage_id: string;
            /**
             * Nature
             * @enum {string}
             */
            nature: "claim" | "record";
            /** Occurred At */
            occurred_at: string | null;
            /** Occurred Precision */
            occurred_precision: ("instant" | "day" | "month" | "year") | null;
            /**
             * Provenance
             * @enum {string}
             */
            provenance: "asserted" | "derived";
            /**
             * Status
             * @enum {string}
             */
            status: "active" | "inactive";
            /**
             * Store
             * @enum {string}
             */
            store: "profile" | "procedural" | "knowledge" | "semantic" | "reported_history";
            /** Topics */
            topics: components["schemas"]["EventTopic"][];
            /** Value */
            value: string | null;
        };
        /**
         * ItemDetail
         * @description One item with the events behind it, oldest first, and every relation
         *     it is on either end of.
         */
        ItemDetail: {
            /** Category */
            category: string | null;
            /** Content */
            content: string;
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            /**
             * Created By Event
             * Format: uuid
             */
            created_by_event: string;
            /** Details */
            details: {
                [key: string]: unknown;
            };
            /** Entities */
            entities: components["schemas"]["EventEntity"][];
            /** Evidence */
            evidence: components["schemas"]["Evidence"][];
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Inactive Reason */
            inactive_reason: ("superseded" | "retired") | null;
            /**
             * Kind
             * @enum {string}
             */
            kind: "profile" | "rule" | "topic_level" | "topic_focus" | "state" | "history" | "decision";
            /**
             * Lineage Id
             * Format: uuid
             */
            lineage_id: string;
            /**
             * Nature
             * @enum {string}
             */
            nature: "claim" | "record";
            /** Occurred At */
            occurred_at: string | null;
            /** Occurred Precision */
            occurred_precision: ("instant" | "day" | "month" | "year") | null;
            /**
             * Provenance
             * @enum {string}
             */
            provenance: "asserted" | "derived";
            /** Relations */
            relations: components["schemas"]["ItemRelation"][];
            /**
             * Status
             * @enum {string}
             */
            status: "active" | "inactive";
            /**
             * Store
             * @enum {string}
             */
            store: "profile" | "procedural" | "knowledge" | "semantic" | "reported_history";
            /** Topics */
            topics: components["schemas"]["EventTopic"][];
            /** Value */
            value: string | null;
        };
        /** ItemRelation */
        ItemRelation: {
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            /**
             * Created By Event
             * Format: uuid
             */
            created_by_event: string;
            /** Ended At */
            ended_at: string | null;
            /**
             * From Item
             * Format: uuid
             */
            from_item: string;
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Kind */
            kind: string;
            /**
             * To Item
             * Format: uuid
             */
            to_item: string;
        };
        /**
         * KnowledgeRow
         * @description One topic, its aliases folded into it.
         */
        KnowledgeRow: {
            focus: components["schemas"]["TopicSetting"] | null;
            /** History Count */
            history_count: number;
            /**
             * Id
             * Format: uuid
             */
            id: string;
            level: components["schemas"]["TopicSetting"] | null;
            /** Name */
            name: string;
            /** Slug */
            slug: string;
            /**
             * State
             * @enum {string}
             */
            state: "canonical" | "provisional";
        };
        /** NowAnswer */
        NowAnswer: {
            /** Items */
            items: components["schemas"]["AnswerItem"][];
            /**
             * Mode
             * @enum {string}
             */
            mode: "now" | "history";
        };
        /**
         * NowRow
         * @description A state as the now view reads it.
         */
        NowRow: {
            /** Category */
            category: string | null;
            /** Content */
            content: string;
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            /**
             * Created By Event
             * Format: uuid
             */
            created_by_event: string;
            /** Details */
            details: {
                [key: string]: unknown;
            };
            /** Entities */
            entities: components["schemas"]["EventEntity"][];
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Inactive Reason */
            inactive_reason: ("superseded" | "retired") | null;
            /**
             * Kind
             * @enum {string}
             */
            kind: "profile" | "rule" | "topic_level" | "topic_focus" | "state" | "history" | "decision";
            /**
             * Last Supported At
             * Format: date-time
             */
            last_supported_at: string;
            /**
             * Lineage Id
             * Format: uuid
             */
            lineage_id: string;
            /**
             * Nature
             * @enum {string}
             */
            nature: "claim" | "record";
            /** Occurred At */
            occurred_at: string | null;
            /** Occurred Precision */
            occurred_precision: ("instant" | "day" | "month" | "year") | null;
            /**
             * Provenance
             * @enum {string}
             */
            provenance: "asserted" | "derived";
            /** Stale */
            stale: boolean;
            /**
             * Status
             * @enum {string}
             */
            status: "active" | "inactive";
            /**
             * Store
             * @enum {string}
             */
            store: "profile" | "procedural" | "knowledge" | "semantic" | "reported_history";
            /** Topics */
            topics: components["schemas"]["EventTopic"][];
            /** Value */
            value: string | null;
        };
        /** ProfileAnswer */
        ProfileAnswer: {
            /** Categories */
            categories: components["schemas"]["ProfileGroup"][];
            /**
             * Mode
             * @enum {string}
             */
            mode: "now" | "history";
            /** Rules */
            rules: components["schemas"]["AnswerItem"][];
        };
        /** ProfileGroup */
        ProfileGroup: {
            /**
             * Category
             * @enum {string}
             */
            category: "identity" | "goals" | "values" | "interests" | "preferences" | "working_style";
            /** Items */
            items: components["schemas"]["AnswerItem"][];
        };
        /**
         * Reason
         * @description One event behind the item.
         */
        Reason: {
            /**
             * Kind
             * @enum {string}
             */
            kind: "supports" | "contradicts";
            /**
             * Occurred At
             * Format: date-time
             */
            occurred_at: string;
            /**
             * Occurred Precision
             * @enum {string}
             */
            occurred_precision: "instant" | "day" | "month" | "year";
            /** Origin */
            origin: string;
            /**
             * Recorded At
             * Format: date-time
             */
            recorded_at: string;
            /** Text */
            text: string;
            /** Verb */
            verb: string;
        };
        /**
         * SearchItem
         * @description An item a search found, as every answer gives it, with where its
         *     words are.
         */
        SearchItem: {
            /** Category */
            category: string | null;
            /** Content */
            content: string;
            /** Details */
            details: {
                [key: string]: unknown;
            };
            /** Evidence */
            evidence: components["schemas"]["Reason"][];
            /** Excerpt */
            excerpt: string;
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Inactive Reason */
            inactive_reason: ("superseded" | "retired") | null;
            /**
             * Kind
             * @enum {string}
             */
            kind: "profile" | "rule" | "topic_level" | "topic_focus" | "state" | "history" | "decision";
            /**
             * Last Supported At
             * Format: date-time
             */
            last_supported_at: string;
            /**
             * Nature
             * @enum {string}
             */
            nature: "claim" | "record";
            /** Occurred At */
            occurred_at: string | null;
            /** Occurred Precision */
            occurred_precision: ("instant" | "day" | "month" | "year") | null;
            /**
             * Provenance
             * @enum {string}
             */
            provenance: "asserted" | "derived";
            /** Stale */
            stale: boolean;
            /**
             * Status
             * @enum {string}
             */
            status: "active" | "inactive";
            /**
             * Store
             * @enum {string}
             */
            store: "profile" | "procedural" | "knowledge" | "semantic" | "reported_history";
            subjects: components["schemas"]["Subjects"];
            support: components["schemas"]["Support"];
            /** Value */
            value: string | null;
        };
        /**
         * Subjects
         * @description What an item is about: its topics, each read through its alias, and
         *     its named things. Both empty for a global item.
         */
        Subjects: {
            /** Entities */
            entities: components["schemas"]["EventEntity"][];
            /** Topics */
            topics: components["schemas"]["EventTopic"][];
        };
        /**
         * Support
         * @description How many events support the item and how many contradict it. These
         *     are the facts a relevance or confidence score would be computed from;
         *     there is no score until the evaluation cases can test one.
         */
        Support: {
            contradicts: components["schemas"]["VerbCounts"];
            supports: components["schemas"]["VerbCounts"];
        };
        /** Topic */
        Topic: {
            /** Alias Of */
            alias_of: string | null;
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Name */
            name: string;
            /**
             * Resolves To
             * Format: uuid
             */
            resolves_to: string;
            /** Slug */
            slug: string;
            /**
             * State
             * @enum {string}
             */
            state: "canonical" | "provisional";
        };
        /**
         * TopicAnswer
         * @description What is known about one asked topic.
         */
        TopicAnswer: {
            /** Asked */
            asked: string;
            focus: components["schemas"]["AnswerItem"] | null;
            /** History */
            history: components["schemas"]["AnswerItem"][];
            /** Known */
            known: boolean;
            level: components["schemas"]["AnswerItem"] | null;
            /** Misconceptions */
            misconceptions: components["schemas"]["AnswerItem"][];
            topic: components["schemas"]["EventTopic"] | null;
        };
        /** TopicRelation */
        TopicRelation: {
            /**
             * Created At
             * Format: date-time
             */
            created_at: string;
            /** Ended At */
            ended_at: string | null;
            /**
             * From Topic
             * Format: uuid
             */
            from_topic: string;
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /** Kind */
            kind: string;
            /**
             * To Topic
             * Format: uuid
             */
            to_topic: string;
        };
        /**
         * TopicSetting
         * @description A topic's active level or focus.
         */
        TopicSetting: {
            /**
             * Id
             * Format: uuid
             */
            id: string;
            /**
             * Set At
             * Format: date-time
             */
            set_at: string;
            /** Value */
            value: string;
        };
        /** TopicsAnswer */
        TopicsAnswer: {
            /**
             * Mode
             * @enum {string}
             */
            mode: "now" | "history";
            /** Topics */
            topics: components["schemas"]["TopicAnswer"][];
        };
        /**
         * VerbCounts
         * @description Events counted per verb kind, every kind present.
         */
        VerbCounts: {
            /** Activity */
            activity: number;
            /** Assertion */
            assertion: number;
            /** Decision */
            decision: number;
            /** Engagement */
            engagement: number;
            /** Exposure */
            exposure: number;
            /** Judgment */
            judgment: number;
        };
        /** WhoAmI */
        WhoAmI: {
            /**
             * Account Id
             * Format: uuid
             */
            account_id: string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    ask_history_brain_api_ask_history_post: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /** Entity */
                    entity?: string | null;
                    /**
                     * Mode
                     * @default now
                     * @enum {string}
                     */
                    mode?: "now" | "history";
                    /** Topic */
                    topic?: string | null;
                };
            };
        };
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["HistoryAnswer"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    ask_now_brain_api_ask_now_post: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /**
                     * Mode
                     * @default now
                     * @enum {string}
                     */
                    mode?: "now" | "history";
                };
            };
        };
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["NowAnswer"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    ask_profile_brain_api_ask_profile_post: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /**
                     * Mode
                     * @default now
                     * @enum {string}
                     */
                    mode?: "now" | "history";
                };
            };
        };
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProfileAnswer"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    ask_topics_brain_api_ask_topics_post: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /**
                     * Mode
                     * @default now
                     * @enum {string}
                     */
                    mode?: "now" | "history";
                    /** Topics */
                    topics: string[];
                };
            };
        };
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["TopicsAnswer"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    list_entities_brain_api_entities_get: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Entity"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    create_entity_brain_api_entities_post: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /** Label */
                    label?: string | null;
                    /** Name */
                    name: string;
                };
            };
        };
        responses: {
            /** @description Successful Response */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Entity"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description It would be the user's second of something unique. */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    list_events_brain_api_events_get: {
        parameters: {
            query?: {
                before?: string | null;
                limit?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Event"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    record_event_brain_api_events_post: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /** Entity */
                    entity?: string | null;
                    /** Evidence */
                    evidence: string;
                    /** Idempotency Key */
                    idempotency_key: string;
                    /** Item */
                    item?: string | null;
                    /** Occurred At */
                    occurred_at?: string | null;
                    /**
                     * Occurred Precision
                     * @default instant
                     * @enum {string}
                     */
                    occurred_precision?: "instant" | "day" | "month" | "year";
                    /** Origin */
                    origin: string;
                    /** Payload */
                    payload: {
                        [key: string]: unknown;
                    };
                    /** Payload Version */
                    payload_version: number;
                    /** Run */
                    run?: string | null;
                    /** Source */
                    source?: string | null;
                    /** Topic */
                    topic?: string | null;
                    /** Verb */
                    verb: string;
                };
            };
        };
        responses: {
            /** @description The user already has an event from this origin with this key: that event, unchanged. */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Event"];
                };
            };
            /** @description Successful Response */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Event"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    health_brain_api_health_get: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Health"];
                };
            };
        };
    };
    list_history_brain_api_history_get: {
        parameters: {
            query?: {
                topic?: string | null;
                entity?: string | null;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["HistoryRow"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    list_items_brain_api_items_get: {
        parameters: {
            query?: {
                kind?: ("profile" | "rule" | "topic_level" | "topic_focus" | "state" | "history" | "decision") | null;
                status?: ("active" | "inactive") | null;
                topic?: string | null;
                entity?: string | null;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Item"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    write_item_brain_api_items_post: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /**
                     * Category
                     * @enum {string}
                     */
                    category: "identity" | "goals" | "values" | "interests" | "preferences" | "working_style";
                    /** Content */
                    content: string;
                    /**
                     * Entities
                     * @default []
                     */
                    entities?: string[];
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "profile";
                    /** Occurred At */
                    occurred_at?: string | null;
                    /**
                     * Occurred Precision
                     * @default instant
                     * @enum {string}
                     */
                    occurred_precision?: "instant" | "day" | "month" | "year";
                    /**
                     * Topics
                     * @default []
                     */
                    topics?: string[];
                } | {
                    /** Content */
                    content: string;
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "rule";
                    /** Occurred At */
                    occurred_at?: string | null;
                    /**
                     * Occurred Precision
                     * @default instant
                     * @enum {string}
                     */
                    occurred_precision?: "instant" | "day" | "month" | "year";
                } | {
                    /** Content */
                    content: string;
                    /**
                     * Entities
                     * @default []
                     */
                    entities?: string[];
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "state";
                    /** Occurred At */
                    occurred_at?: string | null;
                    /**
                     * Occurred Precision
                     * @default instant
                     * @enum {string}
                     */
                    occurred_precision?: "instant" | "day" | "month" | "year";
                    /**
                     * Topics
                     * @default []
                     */
                    topics?: string[];
                } | {
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "topic_level";
                    /** Topic */
                    topic: string;
                    /**
                     * Value
                     * @enum {string}
                     */
                    value: "shallow" | "working" | "solid" | "deep";
                } | {
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "topic_focus";
                    /** Topic */
                    topic: string;
                    /**
                     * Value
                     * @enum {string}
                     */
                    value: "learning" | "steady" | "not_pursuing";
                } | {
                    /** Content */
                    content: string;
                    /**
                     * Entities
                     * @default []
                     */
                    entities?: string[];
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "history";
                    /**
                     * Occurred At
                     * Format: date-time
                     */
                    occurred_at: string;
                    /**
                     * Occurred Precision
                     * @enum {string}
                     */
                    occurred_precision: "instant" | "day" | "month" | "year";
                    /**
                     * Topics
                     * @default []
                     */
                    topics?: string[];
                } | {
                    /**
                     * Misconception
                     * @description What the owner believed and what is right, each 1 to 1800 characters
                     *     once its surrounding whitespace is dropped.
                     */
                    details: {
                        /** Believed */
                        believed: string;
                        /** Correct */
                        correct: string;
                        /**
                         * Misconception
                         * @constant
                         */
                        misconception: true;
                    };
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "history";
                    /** Occurred At */
                    occurred_at?: string | null;
                    /**
                     * Occurred Precision
                     * @default instant
                     * @enum {string}
                     */
                    occurred_precision?: "instant" | "day" | "month" | "year";
                    /** Topic */
                    topic: string;
                } | {
                    /** Content */
                    content: string;
                    /**
                     * DecisionDetails
                     * @description Why a decision was made, and what it was made over.
                     */
                    details: {
                        /** Reasoning */
                        reasoning: string;
                        /**
                         * Rejected
                         * @default []
                         */
                        rejected?: string[];
                    };
                    /**
                     * Entities
                     * @default []
                     */
                    entities?: string[];
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "decision";
                    /** Occurred At */
                    occurred_at?: string | null;
                    /**
                     * Occurred Precision
                     * @default instant
                     * @enum {string}
                     */
                    occurred_precision?: "instant" | "day" | "month" | "year";
                    /**
                     * Topics
                     * @default []
                     */
                    topics?: string[];
                };
            };
        };
        responses: {
            /** @description Successful Response */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ItemDetail"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    read_item_brain_api_items__id__get: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ItemDetail"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    confirm_item_brain_api_items__id__confirm_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ItemDetail"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description The item is not in the state this needs: a superseded version, or not active or retired as the change asks. */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    edit_item_brain_api_items__id__edit_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /** Category */
                    category?: ("identity" | "goals" | "values" | "interests" | "preferences" | "working_style") | null;
                    /** Content */
                    content?: string | null;
                    /** Entities */
                    entities?: string[] | null;
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "profile";
                    /** Topics */
                    topics?: string[] | null;
                } | {
                    /** Content */
                    content?: string | null;
                    /**
                     * Kind
                     * @constant
                     */
                    kind: "rule";
                };
            };
        };
        responses: {
            /** @description Successful Response */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ItemDetail"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description The item is not in the state this needs: a superseded version, or not active or retired as the change asks. */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    replace_item_brain_api_items__id__replace_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /**
                     * Replaced By
                     * Format: uuid
                     */
                    replaced_by: string;
                };
            };
        };
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ItemDetail"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description The item is not in the state this needs: a superseded version, or not active or retired as the change asks. */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    restore_item_brain_api_items__id__restore_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ItemDetail"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description The item is not in the state this needs: a superseded version, or not active or retired as the change asks. */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    retire_item_brain_api_items__id__retire_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ItemDetail"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description The item is not in the state this needs: a superseded version, or not active or retired as the change asks. */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    list_versions_brain_api_items__id__versions_get: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Item"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    list_knowledge_brain_api_knowledge_get: {
        parameters: {
            query?: {
                level?: ("shallow" | "working" | "solid" | "deep") | null;
                focus?: ("learning" | "steady" | "not_pursuing") | null;
                state?: ("canonical" | "provisional") | null;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["KnowledgeRow"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    list_now_brain_api_now_get: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["NowRow"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    search_brain_api_search_get: {
        parameters: {
            query: {
                q: string;
                kind?: ("profile" | "rule" | "topic_level" | "topic_focus" | "state" | "history" | "decision")[];
                include_inactive?: boolean;
                limit?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SearchItem"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    end_relation_brain_api_topic_relations__id__end_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["TopicRelation"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    list_topics_brain_api_topics_get: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Topic"][];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    create_topic_brain_api_topics_post: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /** Name */
                    name: string;
                    /** Slug */
                    slug?: string | null;
                };
            };
        };
        responses: {
            /** @description Successful Response */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Topic"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description It would be the user's second of something unique. */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    add_broader_brain_api_topics__id__broader_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /**
                     * Broader Id
                     * Format: uuid
                     */
                    broader_id: string;
                };
            };
        };
        responses: {
            /** @description Successful Response */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["TopicRelation"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description It would be the user's second of something unique. */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    promote_topic_brain_api_topics__id__promote_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Topic"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    same_as_topic_brain_api_topics__id__same_as_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": {
                    /**
                     * Target Id
                     * Format: uuid
                     */
                    target_id: string;
                };
            };
        };
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Topic"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description A body that does not parse, or a request breaking a rule. */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    separate_topic_brain_api_topics__id__separate_post: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["Topic"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Nothing of this user's at that address. */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    whoami_brain_api_whoami_get: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Successful Response */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["WhoAmI"];
                };
            };
            /** @description No valid token: missing, expired or failing verification, all alike. The body is empty; the caller refreshes and retries. */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
}
