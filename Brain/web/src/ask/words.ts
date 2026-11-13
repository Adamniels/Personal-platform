// What the Ask section says about an item's support and about a conflict, in plain words. There is
// no relevance score, because the brain returns none: support is written as the counts it is
// computed from, as "Response shape" in Brain/docs/brain-architecture.md has it.
import type { components } from "../api/schema";
import { MONTHS } from "../history/when";

type Schemas = components["schemas"];

function total(counts: Schemas["VerbCounts"]): number {
	return Object.values(counts).reduce((sum, count) => sum + count, 0);
}

function entries(count: number): string {
	return count === 1 ? "1 entry" : `${count} entries`;
}

/** The day an instant falls on, in the owner's time, as Now says when a
 * state was confirmed: "12 Nov", with the year only when it is not this
 * one. The months are History's, since Intl's en-GB September is "Sept". */
function lastOn(iso: string, today: Date): string {
	const at = new Date(iso);
	const day = `${at.getDate()} ${(MONTHS[at.getMonth()] ?? "").slice(0, 3)}`;
	return at.getFullYear() === today.getFullYear()
		? day
		: `${day} ${at.getFullYear()}`;
}

/** "Supported by 2 entries, last on 12 Nov", and how many contradict it
 * when any do. */
export function supportLine(
	item: Pick<Schemas["AnswerItem"], "support" | "last_supported_at">,
	today: Date = new Date(),
): string {
	const line = `Supported by ${entries(total(item.support.supports))}, last on ${lastOn(item.last_supported_at, today)}`;
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
