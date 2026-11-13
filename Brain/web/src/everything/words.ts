// What Everything calls the brain's kinds, stores and verbs, in words the owner reads, as the
// slice for Everything settles them. The kinds are the sections an entry belongs to: a state is
// what Now holds, so it is called Now.
import type { components } from "../api/schema";

type Item = components["schemas"]["Item"];
export type Kind = Item["kind"];

export const KINDS: readonly { value: Kind; word: string }[] = [
	{ value: "profile", word: "Profile" },
	{ value: "rule", word: "Rule" },
	{ value: "topic_level", word: "Level" },
	{ value: "topic_focus", word: "Focus" },
	{ value: "state", word: "Now" },
	{ value: "history", word: "History" },
	{ value: "decision", word: "Decision" },
];

export function kindWord(kind: Kind): string {
	return KINDS.find(({ value }) => value === kind)?.word ?? kind;
}

const STORES: Record<Item["store"], string> = {
	profile: "Profile store",
	procedural: "Procedural store",
	knowledge: "Knowledge store",
	semantic: "Semantic store",
	reported_history: "Reported history",
};

export function storeWord(store: Item["store"]): string {
	return STORES[store];
}

/** Who made an item: the owner, by hand, or the brain from evidence. */
export function provenanceWord(provenance: Item["provenance"]): string {
	return provenance === "asserted"
		? "Entered by hand"
		: "Derived from evidence";
}

/** What the owner did, for each verb the memory centre records. */
const OWNER_VERBS: Record<string, string> = {
	asserted: "You entered this",
	edited: "You corrected this",
	retired: "You retired this",
	restored: "You brought this back",
	confirmed: "You confirmed this",
	decided: "You decided this",
};

/** The brain's own origin, which the memory centre's changes carry. */
export const BRAIN_ORIGIN = "brain";

/** An event's verb in plain words: what the owner did, for one the memory
 * centre recorded, or the verb and the feature it came from. */
export function verbWords(verb: string, origin: string): string {
	const own = origin === BRAIN_ORIGIN ? OWNER_VERBS[verb] : undefined;
	return own ?? `${verb[0]?.toUpperCase() ?? ""}${verb.slice(1)}, in ${origin}`;
}

/** Where an event came from: the memory centre, or the feature that sent
 * it. */
export function originWords(origin: string): string {
	return origin === BRAIN_ORIGIN ? "From the memory centre" : `From ${origin}`;
}
