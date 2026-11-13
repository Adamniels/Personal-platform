// What the Ask section says about an item's support and about a conflict, in plain words. There is
// no relevance score, because the brain returns none: support is written as the counts it is
// computed from, as "Response shape" in Brain/docs/brain-architecture.md has it.
import type { components } from "../api/schema";
import { dayOf } from "../everything/words";

type Schemas = components["schemas"];

function total(counts: Schemas["VerbCounts"]): number {
	return Object.values(counts).reduce((sum, count) => sum + count, 0);
}

function entries(count: number): string {
	return count === 1 ? "1 entry" : `${count} entries`;
}

/** "Supported by 2 entries, last on 12 Nov 2026", and how many contradict
 * it when any do. */
export function supportLine(
	item: Pick<Schemas["AnswerItem"], "support" | "last_supported_at">,
): string {
	const line = `Supported by ${entries(total(item.support.supports))}, last on ${dayOf(item.last_supported_at)}`;
	const against = total(item.support.contradicts);
	return against === 0 ? line : `${line}, contradicted by ${entries(against)}`;
}

/** Why one of two conflicting items is preferred, or that neither is, as
 * "Conflicting decisions before consolidation" in
 * Brain/docs/event-model.md has the three outcomes. */
export const REASONS: Record<Schemas["Conflict"]["reason"], string> = {
	"narrower scope": "Narrower scope",
	"newer at equal scope": "Newer at equal scope",
	"incomparable scopes": "No winner: unrelated subjects",
};

/** The address of an item's place in the answer, which a conflict links
 * to. */
export function anchorOf(id: string): string {
	return `answer-${id}`;
}
