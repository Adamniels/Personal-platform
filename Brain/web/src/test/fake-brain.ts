// An in-memory stand in for the brain's item routes, behind the client's fetch, so a test renders
// the real application over the real client and reads which calls it sent. It keeps the rules the
// sections rely on, as "Memory items" in Brain/README.md has them: the list newest first, an edit
// a new version of the same lineage, retiring and restoring the status alone. Anything else it
// answers 404.
import type { ClientEnvironment } from "../api/client";
import type { components } from "../api/schema";

type Item = components["schemas"]["Item"];
type Kind = "profile" | "rule";

export type Call = { method: string; path: string; body: unknown };

/** What the next state changing call answers instead of doing its work. */
type Refusal = { status: number; detail: string };

const API = "/brain/api";

export function fakeBrain() {
	const items: Item[] = [];
	const calls: Call[] = [];
	let refusal: Refusal | null = null;
	let minutes = 0;

	// Each row made a minute after the one before, so the order is never a tie.
	function now(): string {
		minutes += 1;
		return new Date(Date.UTC(2026, 10, 1, 9, minutes)).toISOString();
	}

	function make(
		kind: Kind,
		content: string,
		category: string | null,
		lineage?: string,
	): Item {
		const id = crypto.randomUUID();
		const item: Item = {
			id,
			lineage_id: lineage ?? id,
			kind,
			store: kind === "rule" ? "procedural" : "profile",
			nature: "claim",
			provenance: "asserted",
			content,
			value: null,
			category,
			details: {},
			occurred_at: null,
			occurred_precision: null,
			status: "active",
			inactive_reason: null,
			created_at: now(),
			created_by_event: crypto.randomUUID(),
			topics: [],
			entities: [],
		};
		items.push(item);
		return item;
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
		if (method === "GET" && path === "/items") {
			const kind = url.searchParams.get("kind");
			const status = url.searchParams.get("status");
			const found = items
				.filter((item) => kind === null || item.kind === kind)
				.filter((item) => status === null || item.status === status)
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
		if (path === "/items") {
			const kind = fields.kind as Kind;
			const item = make(kind, content, fields.category ?? null);
			return json(201, detail(item));
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
