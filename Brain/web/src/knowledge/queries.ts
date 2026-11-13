// What the Knowledge section reads and changes: the knowledge view, the topics and their
// relations, and a topic's level, focus and misconceptions, through the routes in "Subjects" and
// "Memory items" in Brain/README.md. Every change refreshes every query under KNOWLEDGE and under
// ITEMS, since a level or a misconception is an item too, so each view showing it shows it as it
// now is.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { components } from "../api/schema";
import { ITEMS } from "../entries/entries";
import { useClient } from "../providers";
import type { Focus, Level } from "./levels";

export type KnowledgeRow = components["schemas"]["KnowledgeRow"];
export type Topic = components["schemas"]["Topic"];
export type TopicRelation = components["schemas"]["TopicRelation"];
export type Item = components["schemas"]["Item"];
type ItemDetail = components["schemas"]["ItemDetail"];

/** The root of every query over topics and the knowledge view. */
export const KNOWLEDGE = ["knowledge"] as const;

/** Every topic that is not an alias, with its level and focus, provisional
 * ones included: the table and the provisional list read the same answer. */
export function useKnowledge() {
	const client = useClient();
	return useQuery({
		queryKey: [...KNOWLEDGE, "view"],
		queryFn: () => client.get("/knowledge"),
	});
}

/** Every topic, aliases included: what a topic's page finds itself, its
 * aliases and the other topics by. */
export function useTopics() {
	const client = useClient();
	return useQuery({
		queryKey: [...KNOWLEDGE, "topics"],
		queryFn: () => client.get("/topics"),
	});
}

/** Every relation between topics that holds, each end as it was given, to
 * be read through `resolves_to`. */
export function useRelations() {
	const client = useClient();
	return useQuery({
		queryKey: [...KNOWLEDGE, "relations"],
		queryFn: () => client.get("/topic-relations"),
	});
}

/** A topic's items of one kind, every version, oldest first, through its
 * aliases, as the brain matches a topic. */
export function useTopicItems(slug: string, kind: "topic_level" | "history") {
	const client = useClient();
	return useQuery({
		queryKey: [...ITEMS, "topic", slug, kind],
		queryFn: () => client.get("/items", { query: { kind, topic: slug } }),
		select: (items) =>
			[...items].sort(
				(a, b) => Date.parse(a.created_at) - Date.parse(b.created_at),
			),
	});
}

/** What a misconception's details hold. */
export type Misconception = { believed: string; correct: string };

export function misconceptionOf(item: Item): Misconception | undefined {
	const { misconception, believed, correct } = item.details;
	return misconception === true &&
		typeof believed === "string" &&
		typeof correct === "string"
		? { believed, correct }
		: undefined;
}

function useRefresh() {
	const queries = useQueryClient();
	return () =>
		Promise.all([
			queries.invalidateQueries({ queryKey: KNOWLEDGE }),
			queries.invalidateQueries({ queryKey: ITEMS }),
		]);
}

export type Setting =
	| { kind: "topic_level"; value: Level }
	| { kind: "topic_focus"; value: Focus };

/** Sets a topic's level or focus. Where it has one already, the brain makes
 * a new version of it, so the earlier value is kept. */
export function useSetOnTopic() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: ({ slug, setting }: { slug: string; setting: Setting }) =>
			client.send<ItemDetail>("POST", "/items", {
				kind: setting.kind,
				topic: slug,
				value: setting.value,
			}),
		onSettled: refresh,
	});
}

/** Saves a topic's level and focus as a form holds them, "" for none,
 * sending only what changed: the same value again would be refused, and a
 * value is never taken away. Resolves with whether anything was sent. */
export function useSaveSettings() {
	const set = useSetOnTopic();
	return async (
		topic: Pick<KnowledgeRow, "slug" | "level" | "focus">,
		draft: { level: string; focus: string },
	): Promise<boolean> => {
		const changes: Setting[] = [];
		if (draft.level !== "" && draft.level !== topic.level?.value) {
			changes.push({ kind: "topic_level", value: draft.level as Level });
		}
		if (draft.focus !== "" && draft.focus !== topic.focus?.value) {
			changes.push({ kind: "topic_focus", value: draft.focus as Focus });
		}
		for (const setting of changes) {
			await set.mutateAsync({ slug: topic.slug, setting });
		}
		return changes.length > 0;
	};
}

export type NewTopic = { name: string; level?: Level; focus?: Focus };

/** Adds a canonical topic, its slug made from its name, then sets its level
 * and focus when they are given. A 409 or 422 on the topic is the form's to
 * show on the name. Whatever happens, the views refresh, since the topic may
 * exist even when setting its level failed. */
export function useAddTopic() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: async ({ name, level, focus }: NewTopic) => {
			const topic = await client.send<Topic>("POST", "/topics", { name });
			if (level) {
				await client.send("POST", "/items", {
					kind: "topic_level",
					topic: topic.slug,
					value: level,
				});
			}
			if (focus) {
				await client.send("POST", "/items", {
					kind: "topic_focus",
					topic: topic.slug,
					value: focus,
				});
			}
			return topic;
		},
		onSettled: refresh,
		meta: { fieldErrors: true },
	});
}

/** Records that he believed something about a topic that is not so: a
 * history entry on it, with what he believed and what is right. A 422 is
 * the form's to show. */
export function useAddMisconception() {
	const client = useClient();
	const refresh = useRefresh();
	return useMutation({
		mutationFn: ({
			slug,
			misconception,
		}: {
			slug: string;
			misconception: Misconception;
		}) =>
			client.send<ItemDetail>("POST", "/items", {
				kind: "history",
				topic: slug,
				details: { misconception: true, ...misconception },
			}),
		onSuccess: refresh,
		meta: { fieldErrors: true },
	});
}

function topicAction(id: string, action: string): string {
	return `/topics/${encodeURIComponent(id)}/${action}`;
}

/** A change to the vocabulary: promoting a topic, marking it the same as
 * another or separating it, placing it under another or taking a relation
 * back. A refusal, such as a loop, raises its toast. */
function useVocabularyChange<Variables>(
	send: (variables: Variables) => Promise<unknown>,
) {
	const refresh = useRefresh();
	return useMutation({ mutationFn: send, onSuccess: refresh });
}

/** Makes a provisional topic canonical. */
export function usePromote() {
	const client = useClient();
	return useVocabularyChange((id: string) =>
		client.send<Topic>("POST", topicAction(id, "promote")),
	);
}

/** Makes the topic an alias of a canonical one, read as it from now on.
 * Nothing that names it changes, so Separate loses nothing. */
export function useSameAs() {
	const client = useClient();
	return useVocabularyChange(
		({ id, targetId }: { id: string; targetId: string }) =>
			client.send<Topic>("POST", topicAction(id, "same-as"), {
				target_id: targetId,
			}),
	);
}

/** Makes an alias its own topic again. */
export function useSeparate() {
	const client = useClient();
	return useVocabularyChange((id: string) =>
		client.send<Topic>("POST", topicAction(id, "separate")),
	);
}

/** Places the topic under a broader one. */
export function usePlaceUnder() {
	const client = useClient();
	return useVocabularyChange(
		({ id, broaderId }: { id: string; broaderId: string }) =>
			client.send<TopicRelation>("POST", topicAction(id, "broader"), {
				broader_id: broaderId,
			}),
	);
}

/** Ends a relation: it stays, with when it ended, and no longer holds. */
export function useEndRelation() {
	const client = useClient();
	return useVocabularyChange((id: string) =>
		client.send<TopicRelation>(
			"POST",
			`/topic-relations/${encodeURIComponent(id)}/end`,
		),
	);
}
