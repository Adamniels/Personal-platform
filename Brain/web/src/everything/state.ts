// An item's state in Everything: one of the brain's four, as the slice settles them. An active
// item is Active, or Still true? when it is a claim about now nothing has supported for sixty
// days; an inactive one is Replaced, when a newer version or a newer decision took its place, or
// Retired, when the owner put it away. "Nothing is destroyed, and only the owner retires" in
// Brain/docs/trust-model.md says there is no third cause.
import type { StatusTone } from "@platform/ui";
import type { components } from "../api/schema";

type Item = components["schemas"]["Item"];

export type StateKey = "active" | "stale" | "replaced" | "retired";

export type State = { key: StateKey; word: string; tone: StatusTone };

const STATES: Record<StateKey, State> = {
	active: { key: "active", word: "Active", tone: "done" },
	stale: { key: "stale", word: "Still true?", tone: "needs-you" },
	replaced: { key: "replaced", word: "Replaced", tone: "idle" },
	retired: { key: "retired", word: "Retired", tone: "idle" },
};

export function stateOf(
	item: Pick<Item, "status" | "inactive_reason"> & { stale: boolean },
): State {
	if (item.status === "active")
		return item.stale ? STATES.stale : STATES.active;
	return item.inactive_reason === "retired" ? STATES.retired : STATES.replaced;
}

export function stateWord(key: StateKey): string {
	return STATES[key].word;
}
