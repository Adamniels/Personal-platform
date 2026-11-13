// What a decision's details hold, as POST /items takes them: why it was made, and what it was
// made over. "Decisions" in Brain/docs/event-model.md.
import type { components } from "../api/schema";

type Item = components["schemas"]["Item"];

export type DecisionDetails = { reasoning: string; rejected: string[] };

/** A decision's reasoning and what it rejected, read from its details, or
 * undefined for anything else. */
export function decisionOf(item: Item): DecisionDetails | undefined {
	if (item.kind !== "decision") return undefined;
	const { reasoning, rejected } = item.details;
	return {
		reasoning: typeof reasoning === "string" ? reasoning : "",
		rejected: Array.isArray(rejected)
			? rejected.filter(
					(option): option is string => typeof option === "string",
				)
			: [],
	};
}
