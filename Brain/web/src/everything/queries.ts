// What Everything reads and changes, through the routes in "Memory items" in Brain/README.md:
// every item, GET /items, with its currency; a search, GET /search; one item with its evidence and
// relations, and its versions; and marking two decisions as contradicting and taking it back.
// Correcting, retiring and bringing back are the shared changes in src/entries/entries.ts. Every
// change refreshes every query under ITEMS, so each section showing the item shows it as it is.
import {
	keepPreviousData,
	useMutation,
	useQuery,
	useQueryClient,
} from "@tanstack/react-query";
import type { components } from "../api/schema";
import { ITEMS } from "../entries/entries";
import { useClient } from "../providers";
import type { Kind } from "./words";

export type ListedItem = components["schemas"]["ListedItem"];
export type ItemDetail = components["schemas"]["ItemDetail"];
type SearchItem = components["schemas"]["SearchItem"];
type ItemRelation = components["schemas"]["ItemRelation"];

/** A row of the table: what an item is and how it stands, from the list or
 * from a search, which also says where its words are. */
export type Row = Pick<
	ListedItem,
	| "id"
	| "kind"
	| "content"
	| "status"
	| "inactive_reason"
	| "stale"
	| "last_supported_at"
	| "topics"
	| "entities"
> & {
	/** A search's excerpt, HTML with the matched words in <mark>. */
	excerpt?: string;
};

/** The most a search answers, the brain's limit, so paging covers all
 * that it gives. */
export const SEARCH_LIMIT = 200;

/** Every item, every version, active or not: the table and an item's page,
 * for what it is related to, read the same answer. */
export function useEverything() {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "everything"],
		queryFn: () => client.get("/items"),
	});
}

function rowOf(found: SearchItem): Row {
	return {
		id: found.id,
		kind: found.kind,
		content: found.content,
		status: found.status,
		inactive_reason: found.inactive_reason,
		stale: found.stale,
		last_supported_at: found.last_supported_at,
		topics: found.subjects.topics,
		entities: found.subjects.entities,
		excerpt: found.excerpt,
	};
}

/** The items holding every word of `words`, of `kind` when one is given,
 * with what is no longer active when `inactive`. While the next answer
 * loads, the one before stays shown. */
export function useSearchItems(
	words: string,
	{ kind, inactive }: { kind: Kind | ""; inactive: boolean },
) {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "search", words, kind, inactive],
		queryFn: () =>
			client.get("/search", {
				query: {
					q: words,
					kind: kind === "" ? undefined : [kind],
					include_inactive: inactive,
					limit: SEARCH_LIMIT,
				},
			}),
		select: (found) => found.map(rowOf),
		enabled: words !== "",
		placeholderData: keepPreviousData,
	});
}

/** One item, with the events behind it and every relation it is on. */
export function useItem(id: string) {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "detail", id],
		queryFn: () => client.get("/items/{id}", { path: { id } }),
		retry: false,
	});
}

/** Every version of the item's entry, oldest first. */
export function useVersions(id: string) {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "versions", id],
		queryFn: () => client.get("/items/{id}/versions", { path: { id } }),
	});
}

function useRefresh() {
	const queries = useQueryClient();
	return () => queries.invalidateQueries({ queryKey: ITEMS });
}

/** Marks two decisions as contradicting each other. A refusal raises its
 * toast. */
export function useMarkContradicting() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: ({ id, other }: { id: string; other: string }) =>
			client.send<ItemRelation>(
				"POST",
				`/items/${encodeURIComponent(id)}/contradicts`,
				{ contradicts: other },
			),
		onSuccess: refresh,
	});
}

/** Takes a contradiction back: the relation stays, with when it ended. */
export function useEndContradiction() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: (id: string) =>
			client.send<ItemRelation>(
				"POST",
				`/item-relations/${encodeURIComponent(id)}/end`,
			),
		onSuccess: refresh,
	});
}
