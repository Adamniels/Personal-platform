// The deck and topics of the last card added, so adding ten cards after a lecture is front, back,
// save, ten times: "The capturing costs almost nothing" in desk/docs/desk-architecture.md. A per
// browser convenience in localStorage, read and written inside try and ignored when unavailable or
// unreadable, never a thing to show an error for.
import type { PickedTopic } from "../topics/topics";

const KEY = "review-last-card";

export type Remembered = { deck_id?: string; topics: PickedTopic[] };

/** What was remembered, keeping only what has the right shape. */
export function readRemembered(): Remembered {
	try {
		const stored: unknown = JSON.parse(localStorage.getItem(KEY) ?? "null");
		if (typeof stored !== "object" || stored === null) return { topics: [] };
		const { deck_id, topics } = stored as Record<string, unknown>;
		return {
			deck_id: typeof deck_id === "string" ? deck_id : undefined,
			topics: Array.isArray(topics) ? topics.filter(isTopic) : [],
		};
	} catch {
		return { topics: [] };
	}
}

export function remember(last: Required<Remembered>): void {
	try {
		localStorage.setItem(KEY, JSON.stringify(last));
	} catch {
		// Storage unavailable or full: the form works without it.
	}
}

function isTopic(value: unknown): value is PickedTopic {
	return (
		typeof value === "object" &&
		value !== null &&
		typeof (value as PickedTopic).slug === "string" &&
		typeof (value as PickedTopic).name === "string"
	);
}
