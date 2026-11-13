// What the Now section reads and changes: the now view, GET /now, and adding and confirming a
// state, through the routes in "Memory items" in Brain/README.md. Correcting and retiring are the
// shared ones in src/entries/entries.ts. Every change refreshes every query under ITEMS.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { components } from "../api/schema";
import { type Entry, ITEMS } from "../entries/entries";
import { useClient } from "../providers";
import { ENTITIES } from "../subjects/subjects";

type NowRow = components["schemas"]["NowRow"];
type Item = components["schemas"]["Item"];
type ItemDetail = components["schemas"]["ItemDetail"];

/** An active state, as the Now section lists it. */
export type NowEntry = Entry & {
	/** When the newest event supporting it happened. */
	lastSupportedAt: string;
	/** Whether that is long enough ago for the brain to ask again. */
	stale: boolean;
};

/** The now view's rows, in its order, newest support first, each with how
 * many versions its entry has had, from every version of every state. */
export function nowEntries(
	rows: readonly NowRow[],
	versions: readonly Item[],
): NowEntry[] {
	const lineages = new Map<string, { first: number; count: number }>();
	for (const item of versions) {
		const made = Date.parse(item.created_at);
		const lineage = lineages.get(item.lineage_id);
		if (lineage) {
			lineage.first = Math.min(lineage.first, made);
			lineage.count += 1;
		} else {
			lineages.set(item.lineage_id, { first: made, count: 1 });
		}
	}
	return rows.map(({ last_supported_at, stale, ...item }) => {
		const lineage = lineages.get(item.lineage_id);
		return {
			...item,
			madeAt: lineage?.first ?? Date.parse(item.created_at),
			versions: lineage?.count ?? 1,
			lastSupportedAt: last_supported_at,
			stale,
		};
	});
}

/** The active states. Every version is fetched beside the view, since that
 * is what says whether an entry has earlier versions, as on Profile. */
export function useNow() {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "now"],
		queryFn: async () => {
			const [rows, versions] = await Promise.all([
				client.get("/now"),
				client.get("/items", { query: { kind: "state" } }),
			]);
			return nowEntries(rows, versions);
		},
	});
}

export type NewState = {
	content: string;
	topics: string[];
	entities: string[];
};

/** Adds a state. A 422 is the form's to show on its field. A new named
 * thing may be made with it, so the named things refresh too. */
export function useAddState() {
	const client = useClient();
	const queries = useQueryClient();
	return useMutation({
		mutationFn: (state: NewState) =>
			client.send<ItemDetail>("POST", "/items", { kind: "state", ...state }),
		onSuccess: () =>
			Promise.all([
				queries.invalidateQueries({ queryKey: ITEMS }),
				queries.invalidateQueries({ queryKey: ENTITIES }),
			]),
		meta: { fieldErrors: true },
	});
}

/** Says a state still holds, which moves when it was last supported. */
export function useConfirm() {
	const client = useClient();
	const queries = useQueryClient();
	return useMutation({
		mutationFn: (id: string) =>
			client.send<ItemDetail>(
				"POST",
				`/items/${encodeURIComponent(id)}/confirm`,
			),
		onSuccess: () => queries.invalidateQueries({ queryKey: ITEMS }),
	});
}
