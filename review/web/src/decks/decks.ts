// The owner's decks, as the Decks tab and the card form read and change them through review's deck
// routes, "What it serves" in review/README.md. Every change refreshes every query under REVIEW, so
// a deck's card count and a card's deck name are never stale.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { components } from "../api/schema";
import { REVIEW, useClient } from "../providers";

export type Deck = components["schemas"]["Deck"];

export const DECKS = [...REVIEW, "decks"] as const;

/** Every deck, by name without letter case, as review answers them. Every view that
 * reads them says itself when they cannot be loaded. */
export function useDecks() {
	const client = useClient();
	return useQuery({
		queryKey: DECKS,
		queryFn: () => client.get("/decks"),
		meta: { ownError: true },
	});
}

/** Refreshes everything under REVIEW once a change has landed, and resolves
 * when it has. */
export function useRefreshReview() {
	const queries = useQueryClient();
	return () => queries.invalidateQueries({ queryKey: REVIEW });
}

/** Makes a deck. A 409, a name the owner has, or a 422 is the form's to show
 * on its field. */
export function useAddDeck() {
	const client = useClient();
	const refresh = useRefreshReview();
	return useMutation({
		mutationFn: (name: string) => client.send<Deck>("POST", "/decks", { name }),
		onSuccess: refresh,
		meta: { fieldErrors: true },
	});
}

/** Renames a deck. A 409 or a 422 is the form's to show on its field. */
export function useRenameDeck() {
	const client = useClient();
	const refresh = useRefreshReview();
	return useMutation({
		mutationFn: ({ id, name }: { id: string; name: string }) =>
			client.send<Deck>("PATCH", deckPath(id), { name }),
		onSuccess: refresh,
		meta: { fieldErrors: true },
	});
}

/** Deletes a deck with no cards. A 409, cards added to it since, raises a
 * toast. */
export function useDeleteDeck() {
	const client = useClient();
	const refresh = useRefreshReview();
	return useMutation({
		mutationFn: (id: string) => client.send("DELETE", deckPath(id)),
		onSuccess: refresh,
	});
}

function deckPath(id: string): string {
	return `/decks/${encodeURIComponent(id)}`;
}

/** "1 card", "3 cards". */
export function cardCount(count: number): string {
	return count === 1 ? "1 card" : `${count} cards`;
}
