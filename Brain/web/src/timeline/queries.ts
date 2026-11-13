// What the Timeline section reads: the events, GET /events, newest recorded first, as "Events" in
// Brain/README.md has it. It sits under ITEMS, so every change to an item, each of which records an
// event, refreshes it.
import { useQuery } from "@tanstack/react-query";
import type { components } from "../api/schema";
import { ITEMS } from "../entries/entries";
import { useClient } from "../providers";

export type Event = components["schemas"]["Event"];

/** The newest events, the brain's first page of fifty. */
export function useEvents() {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "events"],
		queryFn: () => client.get("/events"),
	});
}
