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
