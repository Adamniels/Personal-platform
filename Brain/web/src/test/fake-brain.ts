// An in-memory stand in for the brain's item and topic routes, behind the client's fetch, so a
// test renders the real application over the real client and reads which calls it sent. It keeps
// the rules the sections rely on, as "Memory items" and "Subjects" in Brain/README.md have them:
// the list newest first, an edit a new version of the same lineage, retiring and restoring the
// status alone, a level or focus set again a new version of the one the topic has, and the
// knowledge view one row per topic that is not an alias, and promoting a provisional
// topic. Anything else it answers 404.
import type { ClientEnvironment } from "../api/client";
import type { components } from "../api/schema";

type Item = components["schemas"]["Item"];
type Topic = components["schemas"]["Topic"];
type KnowledgeRow = components["schemas"]["KnowledgeRow"];
type Kind = "profile" | "rule";
type Setting = "topic_level" | "topic_focus";

export type Call = { method: string; path: string; body: unknown };

/** What the next state changing call answers instead of doing its work. */
type Refusal = { status: number; detail: string };

const API = "/brain/api";

const STORES: Record<Kind | Setting, Item["store"]> = {
	profile: "profile",
	rule: "procedural",
	topic_level: "knowledge",
	topic_focus: "profile",
};

export function fakeBrain() {
	const items: Item[] = [];
	const topics: Topic[] = [];
	const calls: Call[] = [];
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

	function json(status: number, body: unknown): Response {
		return new Response(JSON.stringify(body), {
			status,
			headers: { "content-type": "application/json" },
		});
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
		if (method === "GET" && path === "/topic-relations") {
			return json(200, []);
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
				.sort((a, b) => b.created_at.localeCompare(a.created_at));
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
		if (method !== "POST") return json(404, { detail: "No such route" });
		if (refusal) {
			const { status, detail: text } = refusal;
			refusal = null;
			return json(status, { detail: text });
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
			const item = make(kind, content, fields.category ?? null);
			return json(201, detail(item));
		}
		const promote = /^\/topics\/([^/]+)\/promote$/.exec(path);
		const promoted = promote && topics.find(({ id }) => id === promote[1]);
		if (promoted) {
			promoted.state = "canonical";
			return json(200, promoted);
		}
		const change = /^\/items\/([^/]+)\/(edit|retire|restore)$/.exec(path);
		const item = change && items.find(({ id }) => id === change[1]);
		if (!change || !item) return json(404, { detail: "No such item" });
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
		/** The next state changing call answers this instead. */
		refuseNext(status: number, text: string) {
			refusal = { status, detail: text };
		},
		/** The state changing calls sent so far. */
		changes(): Call[] {
			return calls.filter(({ method }) => method !== "GET");
		},
	};
}
