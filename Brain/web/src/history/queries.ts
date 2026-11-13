// What the History section reads and changes: the history view, GET /history, narrowed by a topic
// and a named thing, adding something that happened or a decision, and replacing a decision,
// through the routes in "Memory items" in Brain/README.md. Every change refreshes every query under ITEMS, and the named things, since an
// entry may name a new one.
import {
	keepPreviousData,
	useMutation,
	useQuery,
	useQueryClient,
} from "@tanstack/react-query";
import type { components } from "../api/schema";
import { ITEMS } from "../entries/entries";
import { useClient } from "../providers";
import { ENTITIES } from "../subjects/subjects";
import type { DecisionDetails } from "./decisions";
import type { Precision } from "./when";

export type HistoryRow = components["schemas"]["HistoryRow"];
type ItemDetail = components["schemas"]["ItemDetail"];

/** What the history is narrowed by: a topic's slug and a named thing's
 * name, each "" for any. */
export type HistoryFilters = { topic: string; entity: string };

export const ANY: HistoryFilters = { topic: "", entity: "" };

/** The history, newest first by when each entry happened: the brain
 * answers oldest first, so a page reading it from the top reads it
 * reversed. While a narrower one loads, the one before stays shown. */
export function useHistory(filters: HistoryFilters = ANY) {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "history", filters.topic, filters.entity],
		queryFn: () =>
			client.get("/history", {
				query: {
					topic: filters.topic === "" ? undefined : filters.topic,
					entity: filters.entity === "" ? undefined : filters.entity,
				},
			}),
		select: (rows) => [...rows].reverse(),
		placeholderData: keepPreviousData,
	});
}

function useRefresh() {
	const queries = useQueryClient();
	return () =>
		Promise.all([
			queries.invalidateQueries({ queryKey: ITEMS }),
			queries.invalidateQueries({ queryKey: ENTITIES }),
		]);
}

export type NewHappened = {
	content: string;
	occurred_at: string;
	occurred_precision: Precision;
	topics: string[];
	entities: string[];
};

/** Records something that happened. A 422 is the form's to show. */
export function useAddHappened() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: (entry: NewHappened) =>
			client.send<ItemDetail>("POST", "/items", { kind: "history", ...entry }),
		onSuccess: refresh,
		meta: { fieldErrors: true },
	});
}

export type NewDecision = {
	content: string;
	details: DecisionDetails;
	occurred_at: string;
	occurred_precision: Precision;
	topics: string[];
	entities: string[];
};

/** Records a decision, with its reasoning and what it rejected: a decided
 * event and the claim that it holds. A 422 is the form's to show. */
export function useAddDecision() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: (decision: NewDecision) =>
			client.send<ItemDetail>("POST", "/items", {
				kind: "decision",
				...decision,
			}),
		onSuccess: refresh,
		meta: { fieldErrors: true },
	});
}

/** Replaces a decision with a newer one. Both stay readable: the old one
 * is no longer in force, and says what replaced it. */
export function useReplace() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: ({ id, replacedBy }: { id: string; replacedBy: string }) =>
			client.send<ItemDetail>(
				"POST",
				`/items/${encodeURIComponent(id)}/replace`,
				{ replaced_by: replacedBy },
			),
		onSuccess: refresh,
	});
}
