// An answer as the Ask section lays it out: groups of items, each in the order the brain returned
// it, since that order is part of what is being checked, and never re-sorted here. Who is this
// person? is a group per profile category and then the rules; What do they know? a group per topic
// asked, in the order asked, with what is known about each narrower topic under it; What is the
// history? the subject's group with its narrower topics under it; What is going on now? one list.
import type { components } from "../api/schema";
import { CATEGORIES } from "../profile/profile";
import type { Asked } from "./questions";

type Schemas = components["schemas"];
export type AnswerItem = Schemas["AnswerItem"];

export type Group = {
	/** Unique within the answer. */
	key: string;
	/** Its heading, or none for an answer that is one list. */
	title?: string;
	/** For a narrower topic, the slugs that led to it from the one asked. */
	through?: readonly string[];
	items: readonly AnswerItem[];
	/** What is said in place of the items when there are none. */
	empty: string;
	/** The slugs of the topics related to it, which the answer names only. */
	related: readonly string[];
	/** The narrower topics, under this one. */
	narrower: readonly Group[];
};

type Known = Pick<
	Schemas["TopicAnswer"],
	"level" | "focus" | "misconceptions" | "history"
>;

/** What is known about a topic, in the order the answer gives its fields. */
function knownItems(known: Known): AnswerItem[] {
	return [
		...(known.level ? [known.level] : []),
		...(known.focus ? [known.focus] : []),
		...known.misconceptions,
		...known.history,
	];
}

function heading(asked: string, answered: string | undefined): string {
	return answered === undefined || answered === asked
		? asked
		: `${asked}, answered as ${answered}`;
}

export function groupsOf(asked: Asked): Group[] {
	switch (asked.question) {
		case "profile": {
			const { categories, rules } = asked.answer;
			return [
				...categories.map(({ category, items }) => {
					const label =
						CATEGORIES.find(({ key }) => key === category)?.label ?? category;
					return {
						key: category,
						title: label,
						items,
						empty: `Nothing in ${label}`,
						related: [],
						narrower: [],
					};
				}),
				{
					key: "rules",
					title: "Rules",
					items: rules,
					empty: "No rules",
					related: [],
					narrower: [],
				},
			];
		}
		case "topics":
			return asked.answer.topics.map((topic, index) => {
				const key = `${index}:${topic.asked}`;
				return {
					key,
					title: heading(topic.asked, topic.topic?.slug),
					items: knownItems(topic),
					empty: topic.known
						? `Nothing held about ${topic.topic?.slug ?? topic.asked} yet`
						: `Nothing known about ${topic.asked}`,
					related: topic.related,
					narrower: topic.narrower.map((under) => ({
						key: `${key}:${under.topic.slug}`,
						title: under.topic.slug,
						through: under.via,
						items: knownItems(under),
						empty: `Nothing held about ${under.topic.slug} yet`,
						related: [],
						narrower: [],
					})),
				};
			});
		case "history": {
			const answer = asked.answer;
			const answered = answer.topic?.slug ?? answer.entity?.name;
			return [
				{
					key: answer.asked,
					title: heading(answer.asked, answered),
					items: answer.items,
					empty: answer.known
						? `No history for ${answered ?? answer.asked}`
						: `Nothing known about ${answer.asked}`,
					related: answer.related,
					narrower: answer.narrower.map((under) => ({
						key: `${answer.asked}:${under.topic.slug}`,
						title: under.topic.slug,
						through: under.via,
						items: under.items,
						empty: `No history for ${under.topic.slug}`,
						related: [],
						narrower: [],
					})),
				},
			];
		}
		case "now":
			return [
				{
					key: "now",
					items: asked.answer.items,
					empty: "Nothing is going on",
					related: [],
					narrower: [],
				},
			];
	}
}

/** Where each item is first shown, by its id, the group's key, walking the
 * groups as the page draws them: a group's items, then its narrower
 * groups. An item can be in more than one group, and a link to it goes to
 * the first. */
export function firstPlaces(groups: readonly Group[]): Map<string, string> {
	const places = new Map<string, string>();
	const walk = (group: Group) => {
		for (const { id } of group.items) {
			if (!places.has(id)) places.set(id, group.key);
		}
		group.narrower.forEach(walk);
	};
	groups.forEach(walk);
	return places;
}

/** Every item in the answer, by its id. */
export function itemsById(groups: readonly Group[]): Map<string, AnswerItem> {
	const byId = new Map<string, AnswerItem>();
	const walk = (group: Group) => {
		for (const item of group.items) byId.set(item.id, item);
		group.narrower.forEach(walk);
	};
	groups.forEach(walk);
	return byId;
}
