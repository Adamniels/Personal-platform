// An in-memory stand in for the brain's item and topic routes, behind the client's fetch, so a
// test renders the real application over the real client and reads which calls it sent. It keeps
// the rules the sections rely on, as "Memory items" and "Subjects" in Brain/README.md have them:
// the list newest first, an edit a new version of the same lineage, retiring and restoring the
// status alone, a level or focus set again a new version of the one the topic has, and the
// knowledge view one row per topic that is not an alias, the relations that hold, and promoting a
// provisional topic. The now view is the active states, newest support first, each stale when
// last supported more than sixty days before the real time, and confirming supports one at that
// time; every item listed carries the same currency. History is the active history entries and
// decisions by when each happened, oldest first, and the decisions another replaced, each with the
// one that did, which replacing makes. The timeline is the events a test records, newest recorded
// first, paged by `before` and `before_id` as the brain pages them. The four questions under
// /ask/ answer what a test says they answer, since the page shows the brain's answer as it is and
// retrieval is the brain's to test. Anything else it answers 404.
import type { ClientEnvironment } from "../api/client";
import type { components } from "../api/schema";

type Item = components["schemas"]["Item"];
type Topic = components["schemas"]["Topic"];
type KnowledgeRow = components["schemas"]["KnowledgeRow"];
type TopicRelation = components["schemas"]["TopicRelation"];
type Entity = components["schemas"]["Entity"];
type Event = components["schemas"]["Event"];
type Kind = "profile" | "rule" | "state" | "history" | "decision";
type Precision = NonNullable<Item["occurred_precision"]>;
type Setting = "topic_level" | "topic_focus";

export type Call = { method: string; path: string; body: unknown };

type AnswerItem = components["schemas"]["AnswerItem"];

/** The four questions, by their path under /brain/api. */
export type AskPath =
	| "/ask/profile"
	| "/ask/topics"
	| "/ask/history"
	| "/ask/now";

/** What a question answers: the answer, or a function of the body asked. */
type Reply = unknown | ((body: unknown) => unknown);

/** What the next state changing call answers instead of doing its work. */
type Refusal = { status: number; detail: string };

const API = "/brain/api";
const DAY = 24 * 60 * 60 * 1000;

/** The subjects an entry names: topics by slug, named things by name. */
type Subjects = { topics?: string[]; entities?: string[] };

const STORES: Record<Kind | Setting, Item["store"]> = {
	profile: "profile",
	rule: "procedural",
	state: "semantic",
	history: "reported_history",
	decision: "semantic",
	topic_level: "knowledge",
	topic_focus: "profile",
};

export function fakeBrain() {
	const items: Item[] = [];
	const topics: Topic[] = [];
	const relations: TopicRelation[] = [];
	const entities: Entity[] = [];
	// When each state was last supported, by its id.
	const supported = new Map<string, string>();
	// The decision that replaced each one another did, by its id.
	const replacedBy = new Map<string, string>();
	const events: Event[] = [];
	const calls: Call[] = [];
	const replies = new Map<string, Reply>();
	let refusal: Refusal | null = null;
	let minutes = 0;

	// Each row made a minute after the one before, so the order is never a tie.
	function now(): string {
		minutes += 1;
		return new Date(Date.UTC(2026, 10, 1, 9, minutes)).toISOString();
	}

	function make(
		kind: Kind | Setting,
		content: string,
		category: string | null,
		lineage?: string,
		setting?: { topic: Topic; value: string },
	): Item {
		const id = crypto.randomUUID();
		const item: Item = {
			id,
			lineage_id: lineage ?? id,
			kind,
			store: STORES[kind],
			nature: "claim",
			provenance: "asserted",
			content,
			value: setting?.value ?? null,
			category,
			details: {},
			occurred_at: null,
			occurred_precision: null,
			status: "active",
			inactive_reason: null,
			created_at: now(),
			created_by_event: crypto.randomUUID(),
			topics: setting
				? [
						{
							id: setting.topic.id,
							slug: setting.topic.slug,
							name: setting.topic.name,
						},
					]
				: [],
			entities: [],
		};
		items.push(item);
		return item;
	}

	function makeTopic(
		name: string,
		state: Topic["state"] = "canonical",
		slug = name.toLowerCase().replace(/[^a-z0-9]+/g, "-"),
	): Topic {
		const id = crypto.randomUUID();
		const topic: Topic = {
			id,
			slug,
			name,
			state,
			alias_of: null,
			resolves_to: id,
			created_at: now(),
		};
		topics.push(topic);
		return topic;
	}

	function resolve(topicId: string): string {
		return topics.find(({ id }) => id === topicId)?.resolves_to ?? topicId;
	}

	function activeOn(kind: Setting, topicId: string): Item | undefined {
		return items.find(
			(item) =>
				item.kind === kind &&
				item.status === "active" &&
				item.topics.some(({ id }) => resolve(id) === topicId),
		);
	}

	/** Sets a topic's level or focus, a new version of the one it has. */
	function set(kind: Setting, topic: Topic, value: string): Item {
		const target = topics.find(({ id }) => id === topic.resolves_to) ?? topic;
		const current = activeOn(kind, target.id);
		const content = `${kind === "topic_level" ? "Level" : "Focus"} for ${target.name}: ${value}`;
		if (current) {
			current.status = "inactive";
			current.inactive_reason = "superseded";
		}
		return make(kind, content, null, current?.lineage_id, {
			topic: target,
			value,
		});
	}

	function knowledge(): KnowledgeRow[] {
		const setting = (kind: Setting, topicId: string) => {
			const item = activeOn(kind, topicId);
			return item?.value
				? { id: item.id, value: item.value, set_at: item.created_at }
				: null;
		};
		return topics
			.filter(({ alias_of }) => alias_of === null)
			.sort((a, b) => a.slug.localeCompare(b.slug))
			.map((topic) => ({
				id: topic.id,
				slug: topic.slug,
				name: topic.name,
				state: topic.state,
				level: setting("topic_level", topic.id),
				focus: setting("topic_focus", topic.id),
				history_count: 0,
			}));
	}

	/** The named thing with this name, without letter case, made when the
	 * user has none, as a hand made entry makes one. */
	function entityNamed(name: string): Entity {
		const found = entities.find(
			(entity) => entity.name.toLowerCase() === name.toLowerCase(),
		);
		if (found) return found;
		const entity: Entity = {
			id: crypto.randomUUID(),
			name,
			label: null,
			created_at: now(),
		};
		entities.push(entity);
		return entity;
	}

	/** Names an item's subjects: topics by slug, named things by name. */
	function name(
		item: Item,
		{ topics: slugs = [], entities: names = [] }: Subjects,
	): Item {
		item.topics = slugs.map((slug) => {
			const topic = topics.find((found) => found.slug === slug);
			if (!topic) throw new Error(`no topic ${slug}`);
			return { id: topic.id, slug: topic.slug, name: topic.name };
		});
		item.entities = names.map((wanted) => {
			const { id, name: named } = entityNamed(wanted);
			return { id, name: named };
		});
		return item;
	}

	function makeState(
		content: string,
		{ supportedAt, ...subjects }: Subjects & { supportedAt?: string } = {},
		lineage?: string,
	): Item {
		const item = name(make("state", content, null, lineage), subjects);
		item.nature = "claim";
		supported.set(item.id, supportedAt ?? new Date().toISOString());
		return item;
	}

	/** Something that happened, or a decision, dated as given; a decision
	 * without a date is made now, as the brain dates it by the request. */
	function makeDated(
		kind: "history" | "decision",
		content: string,
		{
			occurredAt,
			precision,
			details = {},
			...subjects
		}: Subjects & {
			occurredAt?: string;
			precision?: Precision;
			details?: Item["details"];
		},
	): Item {
		const item = name(make(kind, content, null), subjects);
		item.nature = kind === "history" ? "record" : "claim";
		item.occurred_at = occurredAt ?? item.created_at;
		item.occurred_precision = precision ?? "instant";
		item.details = details;
		return item;
	}

	/** Replaces a decision with a newer one: the old one inactive, kept,
	 * with the one that replaced it. */
	function replaceWith(old: Item, newer: Item): Item {
		old.status = "inactive";
		old.inactive_reason = "superseded";
		replacedBy.set(old.id, newer.id);
		return old;
	}

	function historyView(url: URL) {
		const slug = url.searchParams.get("topic");
		const entity = url.searchParams.get("entity")?.toLowerCase() ?? null;
		const wanted = topics.find((topic) => topic.slug === slug);
		return items
			.filter((item) => item.kind === "history" || item.kind === "decision")
			.filter((item) => item.status === "active" || replacedBy.has(item.id))
			.filter(
				(item) =>
					slug === null ||
					(wanted !== undefined &&
						item.topics.some(({ id }) => resolve(id) === wanted.resolves_to)),
			)
			.filter(
				(item) =>
					entity === null ||
					item.entities.some(
						({ name: named }) => named.toLowerCase() === entity,
					),
			)
			.sort(
				(a, b) =>
					(a.occurred_at ?? "").localeCompare(b.occurred_at ?? "") ||
					a.created_at.localeCompare(b.created_at),
			)
			.map((item) => ({
				...item,
				replaced_by: replacedBy.get(item.id) ?? null,
			}));
	}

	/** An item with when it was last supported, when it was made unless a
	 * state was supported since, and whether that makes it stale: an active
	 * state last supported more than sixty days before the real time. */
	function withCurrency(item: Item) {
		const last = supported.get(item.id) ?? item.created_at;
		return {
			...item,
			last_supported_at: last,
			stale:
				item.kind === "state" &&
				item.status === "active" &&
				Date.parse(last) < Date.now() - 60 * DAY,
		};
	}

	function nowView() {
		return items
			.filter((item) => item.kind === "state" && item.status === "active")
			.map(withCurrency)
			.sort((a, b) => b.last_supported_at.localeCompare(a.last_supported_at));
	}

	function json(status: number, body: unknown): Response {
		return new Response(JSON.stringify(body), {
			status,
			headers: { "content-type": "application/json" },
		});
	}

	/** The events newest recorded first, the id breaking a tie, as the brain
	 * orders them: `limit` of them, 50 by default, and with `before` and
	 * `before_id` only those after that place. */
	function timeline(url: URL): Event[] {
		const before = url.searchParams.get("before");
		const beforeId = url.searchParams.get("before_id");
		const limit = Number(url.searchParams.get("limit") ?? 50);
		const after = (event: Event) => {
			if (before === null) return true;
			const at = Date.parse(event.recorded_at);
			return (
				at < Date.parse(before) ||
				(at === Date.parse(before) &&
					beforeId !== null &&
					event.id.localeCompare(beforeId) < 0)
			);
		};
		return [...events]
			.sort(
				(a, b) =>
					Date.parse(b.recorded_at) - Date.parse(a.recorded_at) ||
					b.id.localeCompare(a.id),
			)
			.filter(after)
			.slice(0, limit);
	}

	function detail(item: Item) {
		return { ...item, evidence: [], relations: [] };
	}

	function answer(method: string, url: URL, body: unknown): Response {
		const path = url.pathname.slice(API.length);
		if (method === "GET" && path === "/knowledge") {
			return json(200, knowledge());
		}
		if (method === "GET" && path === "/topics") {
			return json(
				200,
				[...topics].sort((a, b) => a.slug.localeCompare(b.slug)),
			);
		}
		if (method === "GET" && path === "/history") {
			return json(200, historyView(url));
		}
		if (method === "GET" && path === "/events") {
			return json(200, timeline(url));
		}
		if (method === "GET" && path === "/now") {
			return json(200, nowView());
		}
		if (method === "GET" && path === "/entities") {
			return json(
				200,
				[...entities].sort((x, y) => x.name.localeCompare(y.name)),
			);
		}
		if (method === "GET" && path === "/topic-relations") {
			return json(200, relations);
		}
		if (method === "GET" && path === "/items") {
			const kind = url.searchParams.get("kind");
			const status = url.searchParams.get("status");
			const slug = url.searchParams.get("topic");
			const wanted = topics.find((topic) => topic.slug === slug);
			const found = items
				.filter((item) => kind === null || item.kind === kind)
				.filter((item) => status === null || item.status === status)
				.filter(
					(item) =>
						slug === null ||
						(wanted !== undefined &&
							item.topics.some(({ id }) => resolve(id) === wanted.resolves_to)),
				)
				.sort((a, b) => b.created_at.localeCompare(a.created_at))
				.map(withCurrency);
			return json(200, found);
		}
		const versions = /^\/items\/([^/]+)\/versions$/.exec(path);
		if (method === "GET" && versions) {
			const item = items.find(({ id }) => id === versions[1]);
			if (!item) return json(404, { detail: "No such item" });
			return json(
				200,
				items
					.filter(({ lineage_id }) => lineage_id === item.lineage_id)
					.sort((a, b) => a.created_at.localeCompare(b.created_at)),
			);
		}
		const one = /^\/items\/([^/]+)$/.exec(path);
		if (method === "GET" && one) {
			const item = items.find(({ id }) => id === one[1]);
			if (!item) return json(404, { detail: "No such item" });
			return json(200, detail(item));
		}
		if (method !== "POST") return json(404, { detail: "No such route" });
		if (refusal) {
			const { status, detail: text } = refusal;
			refusal = null;
			return json(status, { detail: text });
		}
		if (path.startsWith("/ask/")) {
			if (!replies.has(path)) return json(404, { detail: "No such route" });
			const reply = replies.get(path);
			return json(200, typeof reply === "function" ? reply(body) : reply);
		}
		const fields = (body ?? {}) as Partial<Record<string, string>>;
		const content = fields.content ?? "";
		if (path === "/topics") {
			const name = (fields.name ?? "").trim();
			const topic = makeTopic(name);
			return json(201, topic);
		}
		if (path === "/items") {
			const kind = fields.kind as Kind | Setting;
			if (kind === "topic_level" || kind === "topic_focus") {
				const topic = topics.find(({ slug }) => slug === fields.topic);
				if (!topic) return json(422, { detail: "No such topic" });
				return json(201, detail(set(kind, topic, fields.value ?? "")));
			}
			const subjects = body as Subjects;
			if (kind === "state") {
				return json(201, detail(makeState(content, subjects)));
			}
			if (kind === "history" || kind === "decision") {
				const dated = body as {
					occurred_at?: string;
					occurred_precision?: Precision;
					details?: Item["details"];
				};
				return json(
					201,
					detail(
						makeDated(kind, content, {
							...subjects,
							occurredAt: dated.occurred_at,
							precision: dated.occurred_precision,
							details: dated.details,
						}),
					),
				);
			}
			const item = make(kind, content, fields.category ?? null);
			return json(201, detail(item));
		}
		const promote = /^\/topics\/([^/]+)\/promote$/.exec(path);
		const promoted = promote && topics.find(({ id }) => id === promote[1]);
		if (promoted) {
			promoted.state = "canonical";
			return json(200, promoted);
		}
		const replace = /^\/items\/([^/]+)\/replace$/.exec(path);
		if (replace) {
			const old = items.find(({ id }) => id === replace[1]);
			const newer = items.find(({ id }) => id === fields.replaced_by);
			if (!old || !newer) return json(404, { detail: "No such item" });
			return json(200, detail(replaceWith(old, newer)));
		}
		const change = /^\/items\/([^/]+)\/(edit|retire|restore|confirm)$/.exec(
			path,
		);
		const item = change && items.find(({ id }) => id === change[1]);
		if (!change || !item) return json(404, { detail: "No such item" });
		if (change[2] === "confirm") {
			supported.set(item.id, new Date().toISOString());
			return json(200, detail(item));
		}
		if (change[2] === "edit" && item.kind === "state") {
			item.status = "inactive";
			item.inactive_reason = "superseded";
			const next = makeState(
				content,
				{
					topics: item.topics.map(({ slug }) => slug),
					entities: item.entities.map(({ name: named }) => named),
				},
				item.lineage_id,
			);
			return json(201, detail(next));
		}
		if (change[2] === "edit") {
			item.status = "inactive";
			item.inactive_reason = "superseded";
			const next = make(
				item.kind as Kind,
				content,
				item.category,
				item.lineage_id,
			);
			return json(201, detail(next));
		}
		if (change[2] === "retire") {
			item.status = "inactive";
			item.inactive_reason = "retired";
		} else {
			item.status = "active";
			item.inactive_reason = null;
		}
		return json(200, detail(item));
	}

	const env: ClientEnvironment = {
		fetch: async (input, init) => {
			const url = new URL(input, "https://platform.test");
			const method = init.method ?? "GET";
			const body =
				typeof init.body === "string" ? JSON.parse(init.body) : undefined;
			calls.push({ method, path: url.pathname + url.search, body });
			return answer(method, url, body);
		},
		location: {
			pathname: "/brain",
			search: "",
			assign: () => undefined,
		},
	};

	return {
		env,
		calls,
		/** An entry the user already has, made before the test renders. */
		add(kind: Kind, content: string, category: string | null = null): Item {
			return make(kind, content, category);
		},
		/** What is going on now, last supported when given, else now. */
		state(
			content: string,
			options: Subjects & { supportedAt?: string } = {},
		): Item {
			return makeState(content, options);
		},
		/** Something that happened, when and as precisely as given. */
		happened(
			content: string,
			occurredAt: string,
			precision: Precision,
			subjects: Subjects = {},
		): Item {
			return makeDated("history", content, {
				...subjects,
				occurredAt,
				precision,
			});
		},
		/** A decision, with its reasoning and what it rejected. */
		decided(
			content: string,
			details: { reasoning: string; rejected?: string[] },
			options: Subjects & { occurredAt?: string; precision?: Precision } = {},
		): Item {
			return makeDated("decision", content, {
				...options,
				details: { rejected: [], ...details },
			});
		},
		/** Replaces a decision with a newer one, as the API would. */
		replace(old: Item, newer: Item): Item {
			return replaceWith(old, newer);
		},
		/** A named thing the user already has. */
		entity(entityName: string): Entity {
			return entityNamed(entityName);
		},
		/** A topic the user already has, with a level and a focus when
		 * given. */
		topic(
			name: string,
			{
				level,
				focus,
				state,
			}: { level?: string; focus?: string; state?: Topic["state"] } = {},
		): Topic {
			const topic = makeTopic(name, state);
			if (level) set("topic_level", topic, level);
			if (focus) set("topic_focus", topic, focus);
			return topic;
		},
		/** Makes `topic` an alias of `target`, as Same as would. */
		alias(topic: Topic, target: Topic) {
			topic.alias_of = target.id;
			topic.resolves_to = target.id;
		},
		/** Places `narrower` under `broader`, each end as given. */
		placeUnder(narrower: Topic, broader: Topic): TopicRelation {
			const relation: TopicRelation = {
				id: crypto.randomUUID(),
				from_topic: broader.id,
				to_topic: narrower.id,
				kind: "broader_than",
				created_at: now(),
				ended_at: null,
			};
			relations.push(relation);
			return relation;
		},
		/** Sets a level or a focus, as the API would. */
		set(kind: Setting, topic: Topic, value: string): Item {
			return set(kind, topic, value);
		},
		/** Corrects an entry, as an edit through the API would. */
		edit(item: Item, content: string): Item {
			item.status = "inactive";
			item.inactive_reason = "superseded";
			return make(item.kind as Kind, content, item.category, item.lineage_id);
		},
		/** An event on the timeline: what a feature sent, the evidence and
		 * the rest given, or by default an entry made in the memory centre,
		 * recorded and happened now. */
		recorded(evidence: string, overrides: Partial<Event> = {}): Event {
			const at = new Date().toISOString();
			const event: Event = {
				id: crypto.randomUUID(),
				verb: "asserted",
				subject_topic: null,
				topic: null,
				entity: null,
				subject_item: null,
				origin: "brain",
				source: "manual-entry",
				run: null,
				occurred_at: overrides.recorded_at ?? at,
				occurred_precision: "instant",
				recorded_at: at,
				evidence,
				payload: {},
				payload_version: 1,
				idempotency_key: crypto.randomUUID(),
				made_item: null,
				...overrides,
			};
			events.push(event);
			return event;
		},
		/** What a question answers from now on. */
		answers(path: AskPath, reply: Reply) {
			replies.set(path, reply);
		},
		/** The questions asked so far, each its path and body. */
		asked(): Call[] {
			return calls.filter(({ path }) => path.startsWith(`${API}/ask/`));
		},
		/** The next state changing call answers this instead. */
		refuseNext(status: number, text: string) {
			refusal = { status, detail: text };
		},
		/** The state changing calls sent so far. A question is a POST that
		 * changes nothing, so it is not one. */
		changes(): Call[] {
			return calls.filter(
				({ method, path }) =>
					method !== "GET" && !path.startsWith(`${API}/ask/`),
			);
		},
	};
}

const NO_COUNTS = {
	activity: 0,
	assertion: 0,
	decision: 0,
	engagement: 0,
	exposure: 0,
	judgment: 0,
};

/** An item as an answer gives it: an active state entered by hand, last
 * supported by one assertion, unless the test says otherwise. */
export function answerItem(overrides: Partial<AnswerItem> = {}): AnswerItem {
	const at = overrides.last_supported_at ?? "2026-11-12T09:00:00Z";
	return {
		id: crypto.randomUUID(),
		kind: "state",
		store: "semantic",
		nature: "claim",
		provenance: "asserted",
		status: "active",
		inactive_reason: null,
		content: "An entry",
		value: null,
		category: null,
		details: {},
		occurred_at: null,
		occurred_precision: null,
		subjects: { topics: [], entities: [] },
		last_supported_at: at,
		stale: false,
		support: {
			supports: { ...NO_COUNTS, assertion: 1 },
			contradicts: { ...NO_COUNTS },
		},
		evidence: [
			{
				verb: "asserted",
				kind: "supports",
				text: `Entered by hand as state: ${overrides.content ?? "An entry"}`,
				origin: "brain",
				occurred_at: at,
				occurred_precision: "instant",
				recorded_at: at,
			},
		],
		...overrides,
	};
}
