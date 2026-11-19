// One decision to look at on "Waiting for you": an issue Dev flow opened, as the app, for a decision
// the agent marked for the owner on a card it merged. It shows what was chosen, what else was
// possible, why and the reviewer's view, and where the issue, the card and the pull request are;
// it stays until the owner closes the issue on GitHub. Every text is the agent's, the reviewer's or
// the code host's, shown as text. See "The merge check" in devflow/README.md.
import { Card, Status } from "@platform/ui";
import { Inline } from "./format";
import type { WaitingList } from "./waiting";
import styles from "./waiting.module.css";

export type DecisionItem = WaitingList["decisions"][number];

/** Whether a code host's address may be a link: https only, since it is
 * data. */
function linkable(address: string): boolean {
	try {
		return new URL(address).protocol === "https:";
	} catch {
		return false;
	}
}

export function DecisionIssue({ decision }: { decision: DecisionItem }) {
	const project = `${decision.owner}/${decision.project_name}`;
	return (
		<Card
			title={decision.chosen}
			aside={<Status tone="needs-you">To look at</Status>}
			className={styles.item}
			footer={
				<div className={styles.actions}>
					{linkable(decision.issue_url) ? (
						<a
							className={styles.open}
							href={decision.issue_url}
							target="_blank"
							rel="noreferrer"
						>
							Open on GitHub
						</a>
					) : null}
				</div>
			}
		>
			<div className={styles.body}>
				<p className={styles.where}>
					<span>{project}</span> <span>#{decision.issue_number}</span>
				</p>
				<p className={styles.summary}>
					Merged in #{decision.pull_request_number}, for{" "}
					<Inline text={decision.card_title} /> #{decision.card_number}. Close
					the issue once you have looked; a fix is a new card.
				</p>
				<div className={styles.block}>
					<h3 className={styles.label}>The agent decided</h3>
					{decision.alternative === null ? null : (
						<p className={styles.text}>
							<span className={styles.prefix}>Instead of</span>{" "}
							<Inline text={decision.alternative} />
						</p>
					)}
					{decision.why === null ? null : (
						<p className={styles.text}>
							<span className={styles.prefix}>Because</span>{" "}
							<Inline text={decision.why} />
						</p>
					)}
				</div>
				<div className={styles.block}>
					<h3 className={styles.label}>The reviewer</h3>
					<p className={styles.text}>
						{decision.reviewer_view === null ? (
							"The reviewer gave no view on it."
						) : (
							<Inline text={decision.reviewer_view} />
						)}
					</p>
				</div>
			</div>
		</Card>
	);
}
