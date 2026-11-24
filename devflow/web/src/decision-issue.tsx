// One decision to look at on "Waiting for you": an issue Dev flow opened, as the app, for a decision
// the agent marked for the owner on a card it merged, in the detail's frame. It shows what was
// chosen as its title, where it was merged, what else was possible and why on one inset, and the
// reviewer's view, with only a link to the issue; it stays until the owner closes the issue on
// GitHub. Every text is the agent's, the reviewer's or the code host's, shown as text. See "The
// merge check" in devflow/README.md.
import { Status } from "@platform/ui";
import { useId } from "react";
import { Detail } from "./detail";
import { Inline } from "./format";
import { linkable, type WaitingList } from "./waiting";
import styles from "./waiting.module.css";

export type DecisionItem = WaitingList["decisions"][number];

export function DecisionIssue({ decision }: { decision: DecisionItem }) {
	const decidedId = useId();
	return (
		<Detail
			title={decision.chosen}
			where={[
				`${decision.owner}/${decision.project_name}`,
				`issue #${decision.issue_number}`,
			]}
			status={<Status tone="needs-you">To look at</Status>}
			needsYou
			lede={
				<>
					Merged in #{decision.pull_request_number}, for{" "}
					<Inline text={decision.card_title} /> #{decision.card_number}. Close
					the issue once you have looked; a fix is a new card.
				</>
			}
			actions={
				linkable(decision.issue_url) ? (
					<div className={styles.actions}>
						<a
							className={styles.open}
							href={decision.issue_url}
							target="_blank"
							rel="noreferrer"
						>
							Open the issue on GitHub
						</a>
					</div>
				) : null
			}
		>
			{decision.alternative === null && decision.why === null ? null : (
				<div className={styles.block}>
					<h3 id={decidedId} className={styles.label}>
						The agent decided
					</h3>
					<ul aria-labelledby={decidedId} className={styles.decisions}>
						<li className={styles.decision}>
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
						</li>
					</ul>
				</div>
			)}
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
		</Detail>
	);
}
