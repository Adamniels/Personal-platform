// The brain's topics, as the page picks them for a card: read from the brain's own API with the
// owner's session, once per opening of a form, and created there when the owner names a new one.
// Review's backend never calls the brain for topics. A card stores a topic's slug and its name at
// the time, and an alias stands for the topic it resolves to. See "Topics" in the slice's Settled
// section, recorded in "The page" in review/README.md.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { components } from "../api/brain-schema";
import { useBrainClient } from "../providers";

export type Topic = components["schemas"]["Topic"];

/** A topic as a card holds it: the brain's slug, and its name then. */
export type PickedTopic = { slug: string; name: string };

/** A card names one to five topics. */
export const MOST_TOPICS = 5;

/** The brain's topics, cached under this key for every view of the page. */
export const BRAIN_TOPICS = ["brain", "topics"] as const;

/** Every topic the owner has in the brain, aliases included. Read when a
 * form opens, and not again while it is open: a topic made from the form is
 * added to the cache instead. A failure is the picker's to say, so it raises
 * no toast. */
export function useBrainTopics() {
	const brain = useBrainClient();
	return useQuery({
		queryKey: BRAIN_TOPICS,
		queryFn: () => brain.get("/topics"),
		refetchOnWindowFocus: false,
		refetchOnReconnect: false,
		meta: { ownError: true },
	});
}

/** Creates a canonical topic from a name the owner typed, and adds it to the
 * cached list. A 409, a slug the owner has, or a 422 is the field's to
 * show. */
export function useCreateTopic() {
	const brain = useBrainClient();
	const queries = useQueryClient();
	return useMutation({
		mutationFn: (name: string) =>
			brain.send<Topic>("POST", "/topics", { name }),
		onSuccess: (topic) => {
			queries.setQueryData<Topic[]>(BRAIN_TOPICS, (topics) =>
				topics ? [...topics, topic] : [topic],
			);
		},
		meta: { fieldErrors: true },
	});
}

/** The topic an alias resolves to, or the topic itself. */
export function canonical(topic: Topic, topics: readonly Topic[]): Topic {
	return topics.find(({ id }) => id === topic.resolves_to) ?? topic;
}

/** A topic named by its slug as the card should hold it: the topic it
 * resolves to, by its current name, or undefined when the owner no longer
 * has it. */
export function resolveSlug(
	slug: string,
	topics: readonly Topic[],
): PickedTopic | undefined {
	const topic = topics.find((each) => each.slug === slug);
	if (!topic) return undefined;
	const target = canonical(topic, topics);
	return { slug: target.slug, name: target.name };
}

/** The most suggestions listed at once. */
const SHOWN = 8;

/** What typing offers: the topics whose name or slug holds the text,
 * without letter case, each shown as the topic it resolves to and listed
 * once, by name, leaving out those already picked. `create` is the text to
 * offer creating, when no topic has it as its name or slug. */
export function suggestionsFor(
	text: string,
	topics: readonly Topic[],
	picked: readonly PickedTopic[],
): { matches: Topic[]; create: string | undefined } {
	const query = text.trim().toLowerCase();
	if (query === "") return { matches: [], create: undefined };
	const taken = new Set(picked.map(({ slug }) => slug));
	const seen = new Set<string>();
	const matches: Topic[] = [];
	for (const topic of topics) {
		const hit =
			topic.name.toLowerCase().includes(query) || topic.slug.includes(query);
		if (!hit) continue;
		const target = canonical(topic, topics);
		if (taken.has(target.slug) || seen.has(target.id)) continue;
		seen.add(target.id);
		matches.push(target);
	}
	matches.sort((a, b) => a.name.localeCompare(b.name));
	const exact = topics.some(
		(topic) => topic.name.toLowerCase() === query || topic.slug === query,
	);
	return {
		matches: matches.slice(0, SHOWN),
		create: exact ? undefined : text.trim(),
	};
}
