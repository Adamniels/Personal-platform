// What an entry is about, its subjects, as the Now and History sections ask for and show them:
// one canonical topic, by its slug, and one named thing, by its name, which the brain makes when
// the user has none by it, as "Memory items" in Brain/README.md has a hand made entry.
import { useQuery } from "@tanstack/react-query";
import { useTopics } from "../knowledge/queries";
import { useClient } from "../providers";

/** The root of the query over named things. A change that may name a new
 * one refreshes it, as well as the items. */
export const ENTITIES = ["entities"] as const;

/** Every named thing the user has, by name. */
export function useEntities() {
	const client = useClient();
	return useQuery({
		queryKey: ENTITIES,
		queryFn: () => client.get("/entities"),
	});
}

/** The topics an entry may name: the canonical ones, by name, since a
 * provisional topic is the brain's guess and an alias answers as its
 * target. */
export function useCanonicalTopics() {
	const topics = useTopics();
	return {
		...topics,
		data: topics.data
			?.filter((topic) => topic.state === "canonical" && !topic.alias_of)
			.sort((a, b) => a.name.localeCompare(b.name)),
	};
}

/** The subjects a form holds: a topic's slug and a named thing's name, each
 * "" for none. */
export type SubjectDraft = { topic: string; entity: string };

export const NO_SUBJECTS: SubjectDraft = { topic: "", entity: "" };

/** The subjects as an entry names them, in POST /items. */
export function subjectsOf(draft: SubjectDraft): {
	topics: string[];
	entities: string[];
} {
	const entity = draft.entity.trim();
	return {
		topics: draft.topic === "" ? [] : [draft.topic],
		entities: entity === "" ? [] : [entity],
	};
}
