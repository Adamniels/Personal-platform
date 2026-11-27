// What the owner does with a pull request from the waiting list: merge it, after a confirmation,
// or send it back with a comment, both as himself on the code host. The commit the page showed
// travels with a merge, so a pull request that moved on is refused there, never merged. A
// refusal is shown inside the item, in words, and the list is read again, and the nav bar is
// asked to ask again, so its dot follows. See "The pages" and "Acting from the page" in
// devflow/README.md.
import { Button, Dialog, TextArea, useToast } from "@platform/ui";
import { type FormEvent, type ReactNode, useId, useState } from "react";
import { ApiError } from "./api/client";
import type { FeatureMergeApi } from "./feature-merge";
import { askNavAgain } from "./nav-attention";
import type { RunAgainApi } from "./run-again";
import type { SendBackFeature } from "./send-back-feature";
import type { Status } from "./status";
import type { WaitingItem } from "./waiting";
import styles from "./waiting.module.css";

/** How an item is merged and sent back, a stopped project resumed, a
 * feature merged, and a card or feature the owner stopped run again or
 * dropped, Dev flow's API in the page and a test's own in a test. */
export type ActionsApi = FeatureMergeApi &
	RunAgainApi & {
		merge: (id: string, headCommit: string) => Promise<void>;
		sendBack: (id: string, comment: string) => Promise<void>;
		/** Resumes a stopped project, accepting the commit the page showed. */
		resume: (projectId: string, commit: string) => Promise<void>;
		/** Merges a card's pull request, naming the commit the page showed. */
		mergeCard: (
			projectId: string,
			number: number,
			headCommit: string,
		) => Promise<void>;
		/** Sends a card back with the owner's comment, posted as him on its pull
		 * request, which starts its fix. */
		sendBackCard: (
			projectId: string,
			number: number,
			comment: string,
		) => Promise<void>;
		/** Sends a whole feature back with the owner's comment, posted as him on
		 * its pull request, which starts its fix. */
		sendBackFeature: SendBackFeature;
		/** Sends the owner's answers to a card's questions, one for each in
		 * order, for the reading the page showed; posted as him on its issue,
		 * which queues it. */
		answerCard: (
			cardId: string,
			readingId: string,
			answers: GivenAnswer[],
		) => Promise<void>;
	};

/** One answer as it is sent: the recommendation accepted, or the owner's
 * own text. */
export type GivenAnswer = { accept: true } | { text: string };

/** The caller's GitHub link, undefined until the status has been read. */
export type LinkState = Status["github"]["state"] | undefined;

export const NOT_GREEN = "The checks are not green on this commit yet";
export const UNLINKED =
	"Link GitHub on the Projects tab to merge or comment as yourself.";
export const EXPIRED =
	"Your GitHub link has expired. Link it again on the Projects tab to merge or comment.";
const FAILED = "That did not work. Try again.";

export type ItemActionsProps = {
	item: WaitingItem;
	/** Whether the item's main action, Merge, or Comment to run again when
	 * it stopped, is the view's one primary button. */
	primary: boolean;
	link: LinkState;
	api: ActionsApi;
	/** Reads the list again. */
	reload: () => Promise<unknown>;
	/** Open on GitHub, last in the row. */
	open: ReactNode;
};

/**
 * The item's footer: what went wrong last, in words; the comment box when it
 * is open; the row of actions, ending with Open on GitHub; and why Merge, or
 * commenting, cannot be done now. A waiting item has Merge and Send back, a
 * stopped one Comment to run again and no Merge, and one sent back nothing
 * but the link.
 */
export function ItemActions({
	item,
	primary,
	link,
	api,
	reload,
	open,
}: ItemActionsProps) {
	const toast = useToast();
	const lineId = useId();
	const [confirming, setConfirming] = useState(false);
	const [writing, setWriting] = useState(false);
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);

	const waiting = item.kind === "waiting" && !item.sent_back;
	const stopped = item.kind === "stopped" && !item.sent_back;
	const linked = link === "linked";
	// Nothing to say while the link's state is still being read.
	const linkLine =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;
	const mergeLine = !item.can_merge ? NOT_GREEN : linkLine;
	const line = waiting ? mergeLine : stopped ? linkLine : null;

	async function merge() {
		try {
			await api.merge(item.id, item.head_commit);
			setRefusal(null);
			toast.success(`Merged #${item.number}`);
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
			await api.sendBack(item.id, text);
			setRefusal(null);
			setWriting(false);
			setText("");
			toast.success(`Sent back #${item.number}`);
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
		<div className={styles.footer}>
			{refusal === null ? null : (
				<p className={styles.refusal} role="alert">
					{refusal}
				</p>
			)}
			{writing ? (
				<form className={styles.comment} onSubmit={send}>
					<TextArea
						label={stopped ? "A comment runs it again." : "Comment"}
						value={text}
						rows={4}
						onChange={(event) => setText(event.target.value)}
					/>
					<div className={styles.actions}>
						<Button
							type="submit"
							kind={stopped && primary ? "primary" : "secondary"}
							disabled={sending || text.trim() === ""}
						>
							Send
						</Button>
						<Button kind="ghost" onClick={cancel}>
							Cancel
						</Button>
					</div>
				</form>
			) : null}
			<div className={styles.actions}>
				{waiting ? (
					<Button
						kind={primary ? "primary" : "secondary"}
						disabled={mergeLine !== null || !linked}
						aria-describedby={mergeLine === null ? undefined : lineId}
						onClick={() => setConfirming(true)}
					>
						Merge
					</Button>
				) : null}
				{(waiting || stopped) && !writing ? (
					<Button
						kind={stopped && primary ? "primary" : "secondary"}
						disabled={!linked}
						aria-describedby={linkLine === null ? undefined : lineId}
						onClick={() => setWriting(true)}
					>
						{waiting ? "Send back" : "Comment to run again"}
					</Button>
				) : null}
				{open}
			</div>
			{line === null ? null : (
				<p id={lineId} className={styles.why}>
					{line}
				</p>
			)}
			{/* Rendered only while asked for, so a card holds no hidden text. */}
			{waiting && confirming ? (
				<Dialog
					open
					title={`Merge #${item.number} into main?`}
					confirmLabel="Merge"
					onConfirm={merge}
					onCancel={() => setConfirming(false)}
				>
					<div className={styles.confirm}>
						<p className={styles.text}>{item.title}</p>
						<p className={styles.text}>
							<code className={styles.commit}>
								{item.head_commit.slice(0, 7)}
							</code>
						</p>
						{item.decisions.length > 0 ? (
							<p className={styles.text}>
								This confirms {item.decisions.length}{" "}
								{item.decisions.length === 1 ? "decision" : "decisions"} the
								agent made.
							</p>
						) : null}
					</div>
				</Dialog>
			) : null}
		</div>
	);
}

/** A refusal in words: the problem's title and what it adds, or a line of
 * our own for anything that is not an answer from Dev flow. */
export function describe(error: unknown): string {
	if (!(error instanceof ApiError)) return FAILED;
	const { title, detail } = error.problem;
	return detail === null ? title : `${title}. ${detail}`;
}
