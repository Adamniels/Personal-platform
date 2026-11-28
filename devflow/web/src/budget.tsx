// A project's daily budget in tokens, off by default: where it is raised, the card "Usage" on the
// project's settings page, and the item "Waiting for you" shows under Stopped once the project's
// runs reached it, which says until when nothing new starts and links to the budget. Worked out by
// Dev flow as the list is read and never stored, so it goes at midnight in the owner's zone or once
// he raises the budget. See "Settings" under "Cards" and "Stops" in devflow/README.md.
import { Status } from "@platform/ui";
import { Detail, PageLink } from "./detail";
import { dayTime } from "./schedules";
import { settingsPath } from "./sections";
import { amount } from "./tokens";
import type { BudgetReachedItem } from "./waiting";
import styles from "./waiting.module.css";

/** The id of the card "Usage" on a project's settings page. */
export const USAGE_ANCHOR = "usage";

/** Where "Raise the budget" goes: the settings page, at its card. */
export const budgetPath = (id: string) => `${settingsPath(id)}#${USAGE_ANCHOR}`;

/** What "Raise the budget" says, on "Waiting for you", the project's page
 * and Running now. */
export const RAISE_THE_BUDGET = "Raise the budget";

/** What Running now says to do at the usage limit or the daily cap on the
 * API key, linking to the same card, where "Runs on" and the cap are. */
export const CHANGE_HOW_IT_RUNS = "Change how it runs";

/** The item's title on "Waiting for you". */
export const budgetTitle = (item: BudgetReachedItem) =>
	`${item.owner}/${item.project_name} reached its daily budget`;

/** What the project's runs used today, of its budget. */
export const usedOf = (item: Pick<BudgetReachedItem, "used" | "budget">) =>
	`${amount(item.used)} of ${amount(item.budget)} tokens`;

/** A project that reached its daily budget, under Stopped on "Waiting for
 * you": until when nothing new starts, as wall clock in the owner's zone,
 * and the way to raise it. It acts nowhere in the inbox. */
export function BudgetReached({ item }: { item: BudgetReachedItem }) {
	return (
		<Detail
			title={budgetTitle(item)}
			where={[`${item.owner}/${item.project_name}`, usedOf(item)]}
			status={<Status tone="needs-you">Budget reached</Status>}
			needsYou={false}
			lede={`Nothing new starts until ${dayTime(item.lifts_at, item.time_zone)}, midnight in ${item.time_zone}. The run that went over finished.`}
		>
			<div className={styles.actions}>
				<RaiseTheBudget id={item.project_id} />
			</div>
		</Detail>
	);
}

/** "Raise the budget": a secondary, a link to the project's settings page at
 * its card "Usage", followed without a reload. */
export function RaiseTheBudget({ id }: { id: string }) {
	return (
		<PageLink className={styles.secondary} href={budgetPath(id)}>
			{RAISE_THE_BUDGET}
		</PageLink>
	);
}
