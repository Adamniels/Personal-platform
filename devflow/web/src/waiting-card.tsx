// A card of Dev flow's own whose pull request waits for the owner, as "Waiting for you" shows it
// under "Pull requests waiting" beside the runner's: its title and project, the report's summary,
// each decision the agent marked for him, the check Dev flow ran, and Merge, the owner's own
// merge, behind the same confirmation as an item's, and Open on GitHub. The commit shown, the one
// the run built and the check passed on, travels with the merge, so a pull request that moved on is
// refused, never merged. Every text came from the code host or the agent and is shown as text. See
// "The pages" and "What waits" in devflow/README.md.
import { Button, Card, Dialog, Status, useToast } from "@platform/ui";
import { useId, useState } from "react";
import {
	type ActionsApi,
	describe,
	EXPIRED,
	type LinkState,
	UNLINKED,
} from "./actions";
import { Inline } from "./format";
import { askNavAgain } from "./nav-attention";
import { type CardItem, Decisions, linkable } from "./waiting";
import styles from "./waiting.module.css";

export const STOPPED_PROJECT =
	"The project is stopped: resume it before you merge.";

export type WaitingCardProps = {
	card: CardItem;
	/** Whether Merge is the view's one primary button. */
	primary: boolean;
	link: LinkState;
	api: ActionsApi;
	/** Reads the list again. */
	reload: () => Promise<unknown>;
};

/** The check Dev flow ran, in words: what, on which commit, and how it
 * ended. */
export function CheckLine({ check }: { check: CardItem["check"] }) {
	return (
		<p className={styles.text}>
			Dev flow ran{" "}
			{check.command === null ? (
				"the project's check"
			) : (
				<code className={styles.commit}>{check.command}</code>
			)}{" "}
			on <code className={styles.commit}>{check.commit.slice(0, 7)}</code>: exit{" "}
			{check.exit_code}.
		</p>
	);
}

/** One waiting card: its header, what the owner needs to judge it, and its
 * Merge and Open on GitHub. */
export function WaitingCard({
	card,
	primary,
	link,
	api,
	reload,
}: WaitingCardProps) {
	const toast = useToast();
	const lineId = useId();
	const [confirming, setConfirming] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const linkLine =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;
	const line = card.can_merge ? linkLine : STOPPED_PROJECT;
	const project = `${card.owner}/${card.project_name}`;

	async function merge() {
		try {
			await api.mergeCard(card.project_id, card.number, card.head_commit);
			setRefusal(null);
			toast.success(`Merged #${card.pull_request_number}`);
		} catch (error) {
			setRefusal(describe(error));
		}
		setConfirming(false);
		await reload();
		// Whatever it answered, a merge may change what needs the owner.
		askNavAgain();
	}

	return (
		<Card
			title={card.title}
			attention
			aside={<Status tone="needs-you">Waiting for you</Status>}
			className={styles.item}
			footer={
				<div className={styles.footer}>
					{refusal === null ? null : (
						<p className={styles.refusal} role="alert">
							{refusal}
						</p>
					)}
					<div className={styles.actions}>
						<Button
							kind={primary ? "primary" : "secondary"}
							disabled={line !== null || link !== "linked"}
							aria-describedby={line === null ? undefined : lineId}
							onClick={() => setConfirming(true)}
						>
							Merge
						</Button>
						{linkable(card.pull_request_url) ? (
							<a
								className={styles.open}
								href={card.pull_request_url}
								target="_blank"
								rel="noreferrer"
							>
								Open on GitHub
							</a>
						) : null}
					</div>
					{line === null ? null : (
						<p id={lineId} className={styles.why}>
							{line}
						</p>
					)}
					{/* Rendered only while asked for, so a card holds no hidden text. */}
					{confirming ? (
						<Dialog
							open
							title={`Merge #${card.pull_request_number} into ${card.base}?`}
							confirmLabel="Merge"
							onConfirm={merge}
							onCancel={() => setConfirming(false)}
						>
							<div className={styles.confirm}>
								<p className={styles.text}>{card.title}</p>
								<p className={styles.text}>
									<code className={styles.commit}>
										{card.head_commit.slice(0, 7)}
									</code>
								</p>
								{card.decisions.length > 0 ? (
									<p className={styles.text}>
										This confirms {card.decisions.length}{" "}
										{card.decisions.length === 1 ? "decision" : "decisions"} the
										agent made.
									</p>
								) : null}
							</div>
						</Dialog>
					) : null}
				</div>
			}
		>
			<div className={styles.body}>
				<p className={styles.where}>
					<span>{project}</span> <span>#{card.pull_request_number}</span>
				</p>
				{card.summary === "" ? null : (
					<p className={styles.summary}>
						<Inline text={card.summary} />
					</p>
				)}
				{card.decisions.length > 0 ? (
					<Decisions decisions={card.decisions} others={card.other_decisions} />
				) : null}
				<div className={styles.block}>
					<h3 className={styles.label}>The check</h3>
					<CheckLine check={card.check} />
				</div>
			</div>
		</Card>
	);
}
