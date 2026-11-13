// What the Timeline section reads: the events, GET /events, newest recorded first, fifty at a time,
// as "Events" in Brain/README.md has it. Each next page starts after the last event shown, by its
// recorded time and its id, since events recorded together share a time. It sits under ITEMS, so
// every change to an item, each of which records an event, refreshes it.
import { useInfiniteQuery } from "@tanstack/react-query";
import type { components } from "../api/schema";
import { ITEMS } from "../entries/entries";
import { useClient } from "../providers";

export type Event = components["schemas"]["Event"];

/** A page of events, the brain's default. */
export const PAGE = 50;

/** Where a page starts: after this event, or at the newest. */
type Place = { before: string; before_id: string } | null;

/** The events, a page of fifty at a time, the newest first; the next page
 * is there to fetch while the last one came back full. */
export function useEvents() {
	const client = useClient();
	return useInfiniteQuery({
		queryKey: [...ITEMS, "events"],
		queryFn: ({ pageParam }) =>
			client.get("/events", {
				query: pageParam ? { ...pageParam, limit: PAGE } : { limit: PAGE },
			}),
		initialPageParam: null as Place,
		getNextPageParam: (last): Place | undefined => {
			const oldest = last.at(-1);
			if (last.length < PAGE || !oldest) return undefined;
			return { before: oldest.recorded_at, before_id: oldest.id };
		},
	});
}
