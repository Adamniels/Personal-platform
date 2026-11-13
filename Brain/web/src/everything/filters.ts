// Everything's filters, kept in the address as query parameters, so a reload or a shared link
// shows the same list: `q`, the search words; `kind`; `state`, whose default, what is active and
// what may no longer be true, is left out; a subject, as `topic`, a slug, or `entity`, a name; and
// `page`, from 1, left out on the first. A value the page does not know reads as the default.
import { KINDS, type Kind } from "./words";

/** Which states the list shows: the default, "current", is Active and
 * Still true? together; "all" is every one. */
export type StateFilter =
	| "current"
	| "active"
	| "stale"
	| "replaced"
	| "retired"
	| "all";

export const STATE_FILTERS: readonly { value: StateFilter; word: string }[] = [
	{ value: "current", word: "Active and Still true?" },
	{ value: "active", word: "Active" },
	{ value: "stale", word: "Still true?" },
	{ value: "replaced", word: "Replaced" },
	{ value: "retired", word: "Retired" },
	{ value: "all", word: "Any" },
];

export type Subject =
	| { kind: "topic"; slug: string }
	| { kind: "entity"; name: string };

export type Filters = {
	search: string;
	kind: Kind | "";
	state: StateFilter;
	subject: Subject | null;
	/** From 1. */
	page: number;
};

export const DEFAULTS: Filters = {
	search: "",
	kind: "",
	state: "current",
	subject: null,
	page: 1,
};

export function filtersFrom(search: string): Filters {
	const params = new URLSearchParams(search);
	const kind = params.get("kind");
	const state = params.get("state");
	const topic = params.get("topic");
	const entity = params.get("entity");
	const page = Number(params.get("page"));
	return {
		search: params.get("q") ?? "",
		kind: KINDS.some(({ value }) => value === kind) ? (kind as Kind) : "",
		state: STATE_FILTERS.some(({ value }) => value === state)
			? (state as StateFilter)
			: DEFAULTS.state,
		subject: topic
			? { kind: "topic", slug: topic }
			: entity
				? { kind: "entity", name: entity }
				: null,
		page: Number.isInteger(page) && page > 1 ? page : 1,
	};
}

/** The query string for the filters, "" when every one is at its
 * default, each in the order the filter row shows them. */
export function searchOf(filters: Filters): string {
	const params = new URLSearchParams();
	if (filters.search !== "") params.set("q", filters.search);
	if (filters.kind !== "") params.set("kind", filters.kind);
	if (filters.state !== DEFAULTS.state) params.set("state", filters.state);
	if (filters.subject?.kind === "topic")
		params.set("topic", filters.subject.slug);
	if (filters.subject?.kind === "entity") {
		params.set("entity", filters.subject.name);
	}
	if (filters.page > 1) params.set("page", String(filters.page));
	const query = params.toString();
	return query === "" ? "" : `?${query}`;
}

/** The Subject select's value for a subject: "topic:<slug>" or
 * "entity:<name>", or "" for any. */
export function subjectValue(subject: Subject | null): string {
	if (subject === null) return "";
	return subject.kind === "topic"
		? `topic:${subject.slug}`
		: `entity:${subject.name}`;
}

export function subjectFrom(value: string): Subject | null {
	if (value.startsWith("topic:"))
		return { kind: "topic", slug: value.slice(6) };
	if (value.startsWith("entity:")) {
		return { kind: "entity", name: value.slice(7) };
	}
	return null;
}

/** Whether the list must include what is no longer active. */
export function includesInactive(state: StateFilter): boolean {
	return state === "replaced" || state === "retired" || state === "all";
}
