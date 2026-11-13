// The entries the owner makes by hand, profile entries and rules, as the Profile and Rules sections
// read and change them through the item routes, "Memory items" in Brain/README.md. Every change
// refreshes every query under ITEMS, so each list that shows an entry shows it as it now is.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { components } from "../api/schema";
import { useClient } from "../providers";

export type Item = components["schemas"]["Item"];
type ItemDetail = components["schemas"]["ItemDetail"];

export type EntryKind = "profile" | "rule";

/** An active entry, its newest version, as a section lists it. */
export type Entry = Item & {
	/** When its first version was made: where it sits among the others. */
	madeAt: number;
	/** How many versions it has had, this one included. */
	versions: number;
};

export type NewEntry =
	| { kind: "profile"; category: string; content: string }
	| { kind: "rule"; content: string };

/** The root of every query over items. */
export const ITEMS = ["items"] as const;

/** The active entries among every version of every entry of a kind, in the
 * order they were made, oldest first, so a correction keeps its place. Each
 * is its newest version, with how many it has had. */
export function entriesOf(items: readonly Item[]): Entry[] {
	const lineages = new Map<string, { first: number; count: number }>();
	for (const item of items) {
		const made = Date.parse(item.created_at);
		const lineage = lineages.get(item.lineage_id);
		if (lineage) {
			lineage.first = Math.min(lineage.first, made);
			lineage.count += 1;
		} else {
			lineages.set(item.lineage_id, { first: made, count: 1 });
		}
	}
	// The list comes newest first; reversed, it breaks a tie the same way.
	return [...items]
		.reverse()
		.filter((item) => item.status === "active")
		.map((item) => {
			const lineage = lineages.get(item.lineage_id) ?? {
				first: Date.parse(item.created_at),
				count: 1,
			};
			return { ...item, madeAt: lineage.first, versions: lineage.count };
		})
		.sort((a, b) => a.madeAt - b.madeAt);
}

/** A kind's active entries, as entriesOf orders them. Every version is
 * fetched, active or not, since that is what says where an entry sits and
 * whether it has earlier versions. */
export function useEntries(kind: EntryKind) {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "list", kind],
		queryFn: () => client.get("/items", { query: { kind } }),
		select: entriesOf,
	});
}

/** An entry's versions other than the one shown, oldest first, fetched once
 * asked for. */
export function useEarlierVersions(entry: Entry, enabled: boolean) {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "versions", entry.id],
		queryFn: () =>
			client.get("/items/{id}/versions", { path: { id: entry.id } }),
		select: (versions) => versions.filter(({ id }) => id !== entry.id),
		enabled,
	});
}

function useRefresh() {
	const queries = useQueryClient();
	return () => queries.invalidateQueries({ queryKey: ITEMS });
}

/** Adds an entry. A 422 is the form's to show on its field. */
export function useAddEntry() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: (entry: NewEntry) =>
			client.send<ItemDetail>("POST", "/items", entry),
		onSuccess: refresh,
		meta: { fieldErrors: true },
	});
}

/** Corrects an entry's text, which makes a new version of it. A 422 is the
 * form's to show on its field. */
export function useEditEntry() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: ({ entry, content }: { entry: Entry; content: string }) =>
			client.send<ItemDetail>("POST", `${itemPath(entry.id)}/edit`, {
				kind: entry.kind,
				content,
			}),
		onSuccess: refresh,
		meta: { fieldErrors: true },
	});
}

/** Retires an entry, or brings a retired one back. */
export function useRetireEntry() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: (id: string) =>
			client.send<ItemDetail>("POST", `${itemPath(id)}/retire`),
		onSuccess: refresh,
	});
}

export function useRestoreEntry() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: (id: string) =>
			client.send<ItemDetail>("POST", `${itemPath(id)}/restore`),
		onSuccess: refresh,
	});
}

function itemPath(id: string): string {
	return `/items/${encodeURIComponent(id)}`;
}
