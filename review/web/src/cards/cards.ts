// The owner's cards, as the Cards tab reads and changes them through review's card routes, "What it
// serves" in review/README.md. Every change refreshes every query under REVIEW, so the decks' card
// counts follow.
import { useMutation, useQuery } from "@tanstack/react-query";
import { ApiError } from "../api/client";
import type { components } from "../api/schema";
import { useRefreshReview } from "../decks/decks";
import { REVIEW, useClient } from "../providers";
import type { PickedTopic } from "../topics/topics";

export type Card = components["schemas"]["Card"];

/** What the form sends for a card. */
export type CardFields = {
	deck_id: string;
	front: string;
	back: string;
	topics: PickedTopic[];
};

/** The cards, newest first, every deck's or one deck's. */
export function useCards(deck: string | undefined) {
	const client = useClient();
	return useQuery({
		queryKey: [...REVIEW, "cards", deck ?? "all"],
		queryFn: () => client.get("/cards", { query: { deck } }),
	});
}

/** Adds a card. A 422 is the form's to show on its field. */
export function useAddCard() {
	const client = useClient();
	const refresh = useRefreshReview();
	return useMutation({
		mutationFn: (card: CardFields) => client.send<Card>("POST", "/cards", card),
		onSuccess: refresh,
		meta: { fieldErrors: true },
	});
}

/** Changes a card from the version it was read at. A 422 is the form's to
 * show on its field, and a 409, a card changed since, the form's to say. A
 * 409 also refreshes, since it proves the cached card stale, so editing it
 * again starts from the current version. */
export function useEditCard() {
	const client = useClient();
	const refresh = useRefreshReview();
	return useMutation({
		mutationFn: ({
			id,
			version,
			...fields
		}: CardFields & { id: string; version: number }) =>
			client.send<Card>("PATCH", cardPath(id), { version, ...fields }),
		onSuccess: refresh,
		onError: (failure) => {
			if (failure instanceof ApiError && failure.problem.status === 409) {
				return refresh();
			}
		},
		meta: { fieldErrors: true },
	});
}

/** Deletes a card, its review history with it. */
export function useDeleteCard() {
	const client = useClient();
	const refresh = useRefreshReview();
	return useMutation({
		mutationFn: (id: string) => client.send("DELETE", cardPath(id)),
		onSuccess: refresh,
	});
}

function cardPath(id: string): string {
	return `/cards/${encodeURIComponent(id)}`;
}
