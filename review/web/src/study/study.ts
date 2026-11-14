// What studying reads and sends: the queue of due cards, `GET /review/api/queue`, and a grade,
// `POST /review/api/cards/{id}/grades`, see "When a card comes back" in review/README.md.
import { useQuery } from "@tanstack/react-query";
import type { components } from "../api/schema";
import { relativeDue } from "../cards/when";
import { REVIEW, useClient } from "../providers";

export type Queue = components["schemas"]["DueCards"];
export type DueCard = components["schemas"]["CardWithPreviews"];

/** The most cards one queue answers, review's QueueEndpoints.QueueMax. */
export const QUEUE_MAX = 200;

/** What a session studies: one deck, one topic, or, with neither,
 * everything due. */
export type Filter = { deck?: string; topic?: string };

/** The filter a session's address names, `?deck=<id>` or `?topic=<slug>`. */
export function filterOf(search: string): Filter {
	const query = new URLSearchParams(search);
	const filter: Filter = {};
	const deck = query.get("deck");
	const topic = query.get("topic");
	if (deck) filter.deck = deck;
	if (topic) filter.topic = topic;
	return filter;
}

/** The address of a session over the filter. */
export function sessionPath(filter: Filter): string {
	const query = new URLSearchParams();
	if (filter.deck) query.set("deck", filter.deck);
	if (filter.topic) query.set("topic", filter.topic);
	const search = query.toString();
	return search === "" ? "/review/study" : `/review/study?${search}`;
}

/** Everything due, for the start view. Any change refreshes it, being under
 * REVIEW. The view says itself when it cannot be loaded. */
export function useDue() {
	const client = useClient();
	return useQuery({
		queryKey: [...REVIEW, "queue", "all"],
		queryFn: () => client.get("/queue"),
		meta: { ownError: true },
	});
}

/** A deck or a topic with cards due, and how many. */
export type DueGroup = {
	/** The deck's id or the topic's slug. */
	key: string;
	name: string;
	count: number;
	path: string;
};

/** The due cards counted by deck and by topic, each by name without letter
 * case. A card with two topics counts under both, and a topic is named as the
 * first card holding it names it. */
export function groupDue(cards: readonly DueCard[]): {
	decks: DueGroup[];
	topics: DueGroup[];
} {
	const decks = new Map<string, DueGroup>();
	const topics = new Map<string, DueGroup>();
	for (const card of cards) {
		count(decks, card.deck.id, card.deck.name, { deck: card.deck.id });
		for (const topic of card.topics) {
			count(topics, topic.slug, topic.name, { topic: topic.slug });
		}
	}
	return { decks: byName(decks), topics: byName(topics) };
}

function count(
	groups: Map<string, DueGroup>,
	key: string,
	name: string,
	filter: Filter,
) {
	const group = groups.get(key);
	if (group) group.count += 1;
	else groups.set(key, { key, name, count: 1, path: sessionPath(filter) });
}

function byName(groups: Map<string, DueGroup>): DueGroup[] {
	return [...groups.values()].sort((a, b) =>
		a.name.toLowerCase().localeCompare(b.name.toLowerCase()),
	);
}

/** "1 card", "12 cards". */
export function cards(count: number): string {
	return count === 1 ? "1 card" : `${count} cards`;
}

/** When the next card is due, from the queue's own time, as a sentence:
 * "The next card is due in 3 days." */
export function nextDueSentence(nextDue: string, asOf: string): string {
	const when = relativeDue(nextDue, Date.parse(asOf));
	return when === "now"
		? "The next card is due now."
		: `The next card is due ${when}.`;
}
