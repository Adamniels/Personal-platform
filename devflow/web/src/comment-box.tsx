// A comment box for sending work back: the owner's words in a text area, Send while they are not
// empty, and Cancel. What sending does, and what a refusal shows, is the caller's; the box keeps
// his words until a send succeeds, so a refused one can be sent again. Used by "Send back" on a
// feature's page, for a card, a decision and the whole feature. See "Sending a card back" and
// "Fixing a feature" in devflow/README.md.
import { Button, TextArea } from "@platform/ui";
import { type FormEvent, useState } from "react";
import styles from "./waiting.module.css";

export type CommentBoxProps = {
	/** The text area's label, which asks what to write. */
	label: string;
	/** Sends the words, answering whether they were sent, which empties
	 * the box. */
	send: (comment: string) => Promise<boolean>;
	/** Closes the box without sending. */
	cancel: () => void;
};

/** The form: the text area, then Send and Cancel. */
export function CommentBox({ label, send, cancel }: CommentBoxProps) {
	const [text, setText] = useState("");
	const [sending, setSending] = useState(false);

	async function submit(event: FormEvent) {
		event.preventDefault();
		if (text.trim() === "") return;
		setSending(true);
		try {
			if (await send(text)) setText("");
		} finally {
			setSending(false);
		}
	}

	return (
		<form className={styles.comment} onSubmit={submit}>
			<TextArea
				label={label}
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
						setText("");
						cancel();
					}}
				>
					Cancel
				</Button>
			</div>
		</form>
	);
}
