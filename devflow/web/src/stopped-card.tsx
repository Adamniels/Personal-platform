// A card of Dev flow's own that stopped, and a project whose runs keep failing outside their cards,
// as "Waiting for you" shows them under Stopped. A stopped card says why, with "Comment to run
// again", which posts the owner's comment as him on the card's issue, and "Open on GitHub", and no
// Merge, as part 1 does for the runner's stopped items. A failing project says Dev flow cannot run
// its cards, and why, so a broken machine is not silent. Every text in either came from the code
// host, a worker or an agent, and is shown as text. See "Stops" in devflow/README.md.
import { Button, Card, Status, TextArea, useToast } from "@platform/ui";
import { type FormEvent, useId, useState } from "react";
import {
	type ActionsApi,
	describe,
	EXPIRED,
	type LinkState,
	UNLINKED,
} from "./actions";
import { Inline } from "./format";
import { askNavAgain } from "./nav-attention";
import type { FailingProjectItem, StoppedCardItem } from "./waiting";
import styles from "./waiting.module.css";

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

export const RUN_AGAIN = "Comment to run again";

export type StoppedCardProps = {
	card: StoppedCardItem;
	link: LinkState;
	api: ActionsApi;
	/** Reads the list again. */
	reload: () => Promise<unknown>;
	/** Whether a link is safe to make of the code host's address. */
	linkable: (address: string) => boolean;
};

/** One stopped card: why it stopped, the comment that runs it again, and its
 * issue on GitHub. */
export function StoppedCard({
	card,
	link,
	api,
	reload,
	linkable,
}: StoppedCardProps) {
	const toast = useToast();
	const lineId = useId();
	const [writing, setWriting] = useState(false);
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const linked = link === "linked";
	// Nothing to say while the link's state is still being read.
	const line =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;

	async function send(event: FormEvent) {
		event.preventDefault();
		if (text.trim() === "") return;
		setSending(true);
		try {
			await api.sendBackCard(card.project_id, card.number, text);
			setRefusal(null);
			setWriting(false);
			setText("");
			toast.success(`Commented on #${card.number}; it runs again`);
		} catch (error) {
			setRefusal(describe(error));
		} finally {
			setSending(false);
		}
		await reload();
		askNavAgain();
	}

	function cancel() {
		setWriting(false);
		setText("");
	}

	return (
		<Card
			title={card.title}
			aside={<Status tone="failed">Stopped</Status>}
			className={styles.item}
			footer={
				<div className={styles.footer}>
					{refusal === null ? null : (
						<p className={styles.refusal} role="alert">
							{refusal}
						</p>
					)}
					{writing ? (
						<form className={styles.comment} onSubmit={send}>
							<TextArea
								label="A comment runs it again."
								value={text}
								rows={4}
								onChange={(event) => setText(event.target.value)}
							/>
							<div className={styles.actions}>
								<Button type="submit" disabled={sending || text.trim() === ""}>
									Send
								</Button>
								<Button kind="ghost" onClick={cancel}>
									Cancel
								</Button>
							</div>
						</form>
					) : null}
					<div className={styles.actions}>
						{writing ? null : (
							<Button
								disabled={!linked}
								aria-describedby={line === null ? undefined : lineId}
								onClick={() => setWriting(true)}
							>
								{RUN_AGAIN}
							</Button>
						)}
						{linkable(card.web_url) ? (
							<a
								className={styles.open}
								href={card.web_url}
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
				</div>
			}
		>
			<div className={styles.body}>
				<p className={styles.where}>
					<span>
						{card.owner}/{card.project_name}
					</span>{" "}
					<span>#{card.number}</span>
				</p>
				<div className={styles.block}>
					<h3 className={styles.label}>Why it stopped</h3>
					<p className={styles.text}>
						<Inline text={card.reason} />
					</p>
				</div>
				<p className={styles.text}>
					Stopped{" "}
					<time dateTime={card.stopped_at}>
						{DATE_TIME.format(new Date(card.stopped_at))}
					</time>
					. Nothing more runs on it until you comment.
				</p>
			</div>
		</Card>
	);
}

/** A project whose runs keep failing outside their cards: that Dev flow
 * cannot run its cards, the last reason, and when it tries again. */
export function FailingProject({ project }: { project: FailingProjectItem }) {
	return (
		<Card
			title={project.message}
			aside={<Status tone="failed">Cannot run</Status>}
			className={styles.item}
		>
			<div className={styles.body}>
				<p className={styles.where}>
					<span>
						{project.owner}/{project.project_name}
					</span>
				</p>
				<p className={styles.text}>
					{project.failures} runs in a row failed outside their cards, the first{" "}
					<time dateTime={project.since}>
						{DATE_TIME.format(new Date(project.since))}
					</time>
					. Nothing is counted against the cards: Dev flow tries again at{" "}
					<time dateTime={project.wait_until}>
						{DATE_TIME.format(new Date(project.wait_until))}
					</time>
					, and this goes once a run succeeds.
				</p>
			</div>
		</Card>
	);
}
