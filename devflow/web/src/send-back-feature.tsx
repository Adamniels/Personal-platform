// "Send the whole feature back", on a feature's page and on its item on "Waiting for you": a
// comment box whose words are posted as the owner on the feature's pull request, which the next
// read of the project takes as his input and starts a fix of the whole feature with, one that is
// not one of its review's rounds. Offered only for a feature that is ready or waits for him, with
// its pull request. A refusal is shown in words, and what shows the feature is read again, and the
// nav bar asked to ask again. See "Fixing a feature" in devflow/README.md.
import { Button, TextArea, useToast } from "@platform/ui";
import { type FormEvent, useId, useState } from "react";
import { describe, EXPIRED, type LinkState, UNLINKED } from "./actions";
import { askNavAgain } from "./nav-attention";
import styles from "./waiting.module.css";

/** Posts the owner's comment on the feature's pull request, as him. */
export type SendBackFeature = (
	projectId: string,
	number: number,
	comment: string,
) => Promise<void>;

/** Whether a feature in this state, with or without its pull request, may
 * be sent back: it is ready, or it waits for the owner. */
export function canSendBack(
	state: string,
	pullRequest: number | null,
): boolean {
	return (
		pullRequest !== null && (state === "ready" || state === "waiting_for_owner")
	);
}

export type SendBackProps = {
	projectId: string;
	number: number;
	link: LinkState;
	send: SendBackFeature;
	/** Reads again what shows the feature. */
	reload: () => Promise<unknown>;
};

/** The button, and the comment box once it is pressed, with what went wrong
 * last and why it cannot be pressed now. */
export function SendBack({
	projectId,
	number,
	link,
	send,
	reload,
}: SendBackProps) {
	const toast = useToast();
	const lineId = useId();
	const [writing, setWriting] = useState(false);
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	// Nothing to say while the link's state is still being read.
	const line =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (text.trim() === "") return;
		setSending(true);
		try {
			await send(projectId, number, text);
			setRefusal(null);
			setWriting(false);
			setText("");
			toast.success(`Sent back feature #${number}`);
		} catch (error) {
			setRefusal(describe(error));
		} finally {
			setSending(false);
		}
		await reload();
		askNavAgain();
	}

	return (
		<div className={styles.footer}>
			{refusal === null ? null : (
				<p className={styles.refusal} role="alert">
					{refusal}
				</p>
			)}
			{writing ? (
				<form className={styles.comment} onSubmit={submit}>
					<TextArea
						label="What should the developer change in the whole feature?"
						value={text}
						rows={4}
						onChange={(event) => setText(event.target.value)}
					/>
					<div className={styles.actions}>
						<Button type="submit" disabled={sending || text.trim() === ""}>
							Send
						</Button>
						<Button
							kind="ghost"
							onClick={() => {
								setWriting(false);
								setText("");
							}}
						>
							Cancel
						</Button>
					</div>
				</form>
			) : (
				<div className={styles.actions}>
					<Button
						disabled={link !== "linked"}
						aria-describedby={line === null ? undefined : lineId}
						onClick={() => setWriting(true)}
					>
						Send the whole feature back
					</Button>
				</div>
			)}
			{line === null ? null : (
				<p id={lineId} className={styles.why}>
					{line}
				</p>
			)}
		</div>
	);
}
