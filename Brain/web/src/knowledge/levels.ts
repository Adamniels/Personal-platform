// A topic's level and focus as words, never as a number, from "How level is represented" in
// Brain/docs/knowledge-model.md: four ordered levels, each with its meaning, and three focus values.
import type { components } from "../api/schema";

type KnowledgeRow = components["schemas"]["KnowledgeRow"];

export type Level = "shallow" | "working" | "solid" | "deep";
export type Focus = "learning" | "steady" | "not_pursuing";

/** The levels in their order, shallowest first, each with what it means. */
export const LEVELS: readonly {
	value: Level;
	word: string;
	meaning: string;
}[] = [
	{ value: "shallow", word: "Shallow", meaning: "Knows the vocabulary" },
	{ value: "working", word: "Working", meaning: "Can use it with help" },
	{
		value: "solid",
		word: "Solid",
		meaning: "Can use it unaided and explain it",
	},
	{
		value: "deep",
		word: "Deep",
		meaning: "Knows the internals and could teach it",
	},
];

export const FOCUSES: readonly { value: Focus; word: string }[] = [
	{ value: "learning", word: "Learning" },
	{ value: "steady", word: "Steady" },
	{ value: "not_pursuing", word: "Not pursuing" },
];

/** What a topic with no level or no focus shows. */
export const NOT_SET = "Not set";

export function levelWord(value: string | null | undefined): string {
	return LEVELS.find((level) => level.value === value)?.word ?? NOT_SET;
}

export function focusWord(value: string | null | undefined): string {
	return FOCUSES.find((focus) => focus.value === value)?.word ?? NOT_SET;
}

export function levelMeaning(value: string): string | undefined {
	return LEVELS.find((level) => level.value === value)?.meaning;
}

/** Where a level sits in the order, or undefined when it is not set. */
export function levelRank(
	value: string | null | undefined,
): number | undefined {
	const index = LEVELS.findIndex((level) => level.value === value);
	return index === -1 ? undefined : index;
}

/** When a topic's level or focus was last set, the later of the two, or
 * undefined when it has neither. */
export function lastSet(row: KnowledgeRow): string | undefined {
	const times = [row.level?.set_at, row.focus?.set_at].filter(
		(time): time is string => time !== undefined,
	);
	return times.sort((a, b) => Date.parse(a) - Date.parse(b)).at(-1);
}
