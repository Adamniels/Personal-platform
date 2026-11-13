// Generated from contracts/brain/openapi.json by tools/generate-schema.mjs, with
// `npm run generate:api`. Do not edit: src/api/schema.test.ts fails when it is stale.

export interface paths {
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
         *     profile, with a category, or rule, which is global. 422 for an unknown
         *     kind or category, content that is blank or over 4000 characters, a rule
         *     naming a subject, or a topic the user does not have. A named thing the
         *     user does not have is created.
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
