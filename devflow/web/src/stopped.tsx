// A project Dev flow stopped because something landed on a branch it watches, its default branch or
// an open feature's, that Dev flow did not put there, as "Waiting for you" shows it under Stopped:
// the branch, what landed, who did it and when, with a link to the commit; or, for a feature's
// branch deleted, that it was, with no commit, since none is left; and "I have looked, resume",
// behind a confirmation that says resuming accepts it. The commit shown travels with the resume, so
// something that landed since is never accepted unseen. Every text from the code host, the actor
// among them, is shown as text. See "Watching branches" in devflow/README.md.
import { Button, Card, Dialog, Status, useToast } from "@platform/ui";
import { useState } from "react";
import { type ActionsApi, describe } from "./actions";
import { askNavAgain } from "./nav-attention";
import type { OwnItem } from "./waiting";
import styles from "./waiting.module.css";

/** What landed, as a verb with "by" after it. */
const LANDED: Record<string, string> = {
	push: "pushed",
	force_push: "force pushed",
	branch_creation: "branch created",
	branch_deletion: "branch deleted",
	pull_request_merge: "merged",
	merge_queue_merge: "merged from a merge queue",
};

/** Who did it, or what to say when the code host named nobody. */
const NOBODY = "an account GitHub no longer names";

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

export const stoppedTitle = (branch: string) =>
	`Something landed on ${branch} that Dev flow did not put there`;

export type StoppedProjectProps = {
	stop: OwnItem;
	api: ActionsApi;
	/** Reads the list again. */
	reload: () => Promise<unknown>;
	/** Whether a link is safe to make of the code host's address. */
	linkable: (address: string) => boolean;
};

/** One stopped project: its card, its resume, and the confirmation. */
export function StoppedProject({
	stop,
	api,
	reload,
	linkable,
}: StoppedProjectProps) {
	const toast = useToast();
	const [confirming, setConfirming] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const project = `${stop.owner}/${stop.project_name}`;
	const short = stop.commit.slice(0, 7);
	const verb = LANDED[stop.activity_type] ?? "changed";
	// A deleted branch is at no commit: there is nothing to show or open.
	const deleted = stop.activity_type === "branch_deletion";
	const who = stop.actor ?? NOBODY;

	async function resume() {
		try {
			await api.resume(stop.project_id, stop.commit);
			setRefusal(null);
			toast.success(`Resumed ${project}`);
		} catch (error) {
			setRefusal(describe(error));
		}
		setConfirming(false);
		await reload();
		askNavAgain();
	}

	return (
		<Card
			title={stoppedTitle(stop.branch)}
			aside={<Status tone="failed">Stopped</Status>}
			className={styles.item}
			footer={
				<div className={styles.footer}>
					{refusal === null ? null : (
						<p className={styles.refusal} role="alert">
							{refusal}
						</p>
					)}
					<div className={styles.actions}>
						<Button onClick={() => setConfirming(true)}>
							I have looked, resume
						</Button>
						{!deleted && linkable(stop.commit_url) ? (
							<a
								className={styles.open}
								href={stop.commit_url}
								target="_blank"
								rel="noreferrer"
							>
								Open the commit on GitHub
							</a>
						) : null}
					</div>
					{/* Rendered only while asked for, so a card holds no hidden text. */}
					{confirming ? (
						<Dialog
							open
							title={`Resume ${project}?`}
							confirmLabel="Resume"
							onConfirm={resume}
							onCancel={() => setConfirming(false)}
						>
							<div className={styles.confirm}>
								<p className={styles.text}>
									{deleted ? (
										<>
											Resuming accepts that {stop.branch} was deleted by {who}.
										</>
									) : (
										<>
											Resuming accepts what landed on {stop.branch}:{" "}
											<code className={styles.commit}>{short}</code>, {verb} by{" "}
											{who}.
										</>
									)}
								</p>
								<p className={styles.text}>
									Dev flow hands out work on this project again.
								</p>
							</div>
						</Dialog>
					) : null}
				</div>
			}
		>
			<div className={styles.body}>
				<p className={styles.where}>
					<span>{project}</span>
				</p>
				<p className={styles.text}>
					{deleted ? (
						<span>{stop.branch}</span>
					) : (
						<code className={styles.commit}>{short}</code>
					)}{" "}
					{verb} by {stop.actor === null ? NOBODY : <span>{stop.actor}</span>},{" "}
					<time dateTime={stop.landed_at}>
						{DATE_TIME.format(new Date(stop.landed_at))}
					</time>
				</p>
				<p className={styles.text}>
					Dev flow hands out no work on this project until you have looked and
					resume it.
				</p>
			</div>
		</Card>
	);
}
