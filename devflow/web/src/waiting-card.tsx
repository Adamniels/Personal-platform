// A card of Dev flow's own whose pull request waits for the owner, as "Waiting for you" shows it
// under "Pull requests waiting" beside the runner's, in the detail's frame: where it is, its title,
// the report's summary, the tiles of the check Dev flow ran, the reviewer's verdict on the commit
// and the decisions, then why it waits, each decision the agent marked for him, what the reviewer
// said and its pictures; and Merge, the owner's own merge, behind a confirmation, Send back, his
// comment posted as him on the pull request, which starts the card's fix, and Open on GitHub. The
// commit shown, the one the run built and the check passed on, travels with the merge, so a pull
// request that moved on is refused, never merged. Every text came from the code host or the agent
// and is shown as text. See "The pages" and "What waits" in devflow/README.md.
import { Button, Dialog, Status, TextArea, useToast } from "@platform/ui";
import { type FormEvent, useId, useState } from "react";
import {
	type ActionsApi,
	describe,
	EXPIRED,
	type LinkState,
	UNLINKED,
} from "./actions";
import {
	ActionNote,
	count,
	Detail,
	decisionsTile,
	reviewTile,
	short,
	type Tile,
	Tiles,
} from "./detail";
import { Inline } from "./format";
import { askNavAgain } from "./nav-attention";
import { cardScreenshot, Screenshots } from "./screenshots";
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

/** The card's tiles: the check Dev flow ran, the review of the commit, and
 * the decisions. */
export function cardTiles(card: CardItem): Tile[] {
	const { check } = card;
	return [
		{
			label: "The check",
			tone: check.exit_code === 0 ? "done" : "failed",
			status: check.exit_code === 0 ? "Passed" : "Failed",
			line: `${check.command ?? "the project's check"} · exit ${check.exit_code}`,
		},
		reviewTile(card.review, "no review of this commit yet"),
		decisionsTile(card.decisions.length, card.other_decisions),
	];
}

/** "Merging confirms the 2 decisions", beside Merge. */
export const mergeConfirms = (decisions: number) =>
	`Merging confirms the ${count(decisions, "decision")}`;

/** One waiting card: its frame, what the owner needs to judge it, and its
 * Merge, Send back and Open on GitHub. */
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
	const [writing, setWriting] = useState(false);
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const linkLine =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;
	const line = card.can_merge ? linkLine : STOPPED_PROJECT;

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

	async function send(event: FormEvent) {
		event.preventDefault();
		if (text.trim() === "") return;
		setSending(true);
		try {
			await api.sendBackCard(card.project_id, card.number, text);
			setRefusal(null);
			setWriting(false);
			setText("");
			toast.success(`Sent back #${card.pull_request_number}`);
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
		<Detail
			title={card.title}
			where={[
				`${card.owner}/${card.project_name}`,
				`pull request #${card.pull_request_number}`,
				`${short(card.head_commit)} → ${card.base}`,
			]}
			status={<Status tone="needs-you">Waiting for you</Status>}
			needsYou
			lede={card.summary === "" ? null : <Inline text={card.summary} />}
			actions={
				<>
					{refusal === null ? null : (
						<p className={styles.refusal} role="alert">
							{refusal}
						</p>
					)}
					{writing ? (
						<form className={styles.comment} onSubmit={send}>
							<TextArea
								label="What should the developer change?"
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
						<Button
							kind={primary ? "primary" : "secondary"}
							disabled={line !== null || link !== "linked"}
							aria-describedby={line === null ? undefined : lineId}
							onClick={() => setConfirming(true)}
						>
							Merge
						</Button>
						{writing ? null : (
							<Button
								disabled={link !== "linked"}
								aria-describedby={linkLine === null ? undefined : lineId}
								onClick={() => setWriting(true)}
							>
								Send back
							</Button>
						)}
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
						{card.decisions.length > 0 ? (
							<ActionNote>{mergeConfirms(card.decisions.length)}</ActionNote>
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
										{short(card.head_commit)}
									</code>
								</p>
								{card.decisions.length > 0 ? (
									<p className={styles.text}>
										This confirms {count(card.decisions.length, "decision")} the
										agent made.
									</p>
								) : null}
							</div>
						</Dialog>
					) : null}
				</>
			}
		>
			<Tiles tiles={cardTiles(card)} />
			{card.reason === null ? null : (
				<div className={styles.block}>
					<h3 className={styles.label}>Why it waits</h3>
					<p className={styles.text}>{card.reason}</p>
				</div>
			)}
			{card.decisions.length > 0 ? (
				<Decisions decisions={card.decisions} others={card.other_decisions} />
			) : null}
			{card.review === null || card.review.summary === "" ? null : (
				<div className={styles.block}>
					<h3 className={styles.label}>The reviewer</h3>
					<p className={styles.text}>
						<Inline text={card.review.summary} />
					</p>
				</div>
			)}
			{card.screenshots === null ? null : (
				<Screenshots
					address={(name) => cardScreenshot(card.project_id, card.number, name)}
					groups={card.screenshots.groups}
					note={card.screenshots.note}
				/>
			)}
		</Detail>
	);
}
