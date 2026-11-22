// A card of Dev flow's own whose reading found questions, as "Waiting for you" shows it under
// "Questions before starting": its title and feature, each question with the reader's
// recommendation and why, "Accept" and a box for another answer, and one "Send answers", which
// posts them as the owner on its issue and queues it. What he typed is kept until a send succeeds.
// Its title and every question came from the code host or the reader, and are shown as text. See
// "Reading a card" under "Jobs" in devflow/README.md.
import { Button, Card, Status, TextArea, useToast } from "@platform/ui";
import { type FormEvent, useId, useState } from "react";
import {
	type ActionsApi,
	describe,
	EXPIRED,
	type GivenAnswer,
	type LinkState,
	UNLINKED,
} from "./actions";
import { Inline } from "./format";
import { askNavAgain } from "./nav-attention";
import type { QuestionCardItem } from "./waiting";
import styles from "./waiting.module.css";

export type QuestionCardProps = {
	card: QuestionCardItem;
	link: LinkState;
	api: ActionsApi;
	/** Reads the list again. */
	reload: () => Promise<unknown>;
	/** Whether a link is safe to make of the code host's address. */
	linkable: (address: string) => boolean;
};

/** One question's answer as the page holds it: the recommendation accepted,
 * or what the owner typed. */
type Draft = { accepted: boolean; text: string };

/** The answer to send for a draft, or undefined while it has none. */
function given(draft: Draft): GivenAnswer | undefined {
	if (draft.accepted) return { accept: true };
	const text = draft.text.trim();
	return text === "" ? undefined : { text };
}

/** One card that waits for answers: each question to answer, and one send
 * for all of them. */
export function QuestionCard({
	card,
	link,
	api,
	reload,
	linkable,
}: QuestionCardProps) {
	const toast = useToast();
	const lineId = useId();
	const [drafts, setDrafts] = useState<Draft[]>(() =>
		card.questions.map(() => ({ accepted: false, text: "" })),
	);
	const [sending, setSending] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const linked = link === "linked";
	// Nothing to say while the link's state is still being read.
	const line =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;
	const answers = drafts.map(given);
	const complete = answers.every((answer) => answer !== undefined);

	const change = (at: number, draft: Draft) =>
		setDrafts((before) =>
			before.map((each, index) => (index === at ? draft : each)),
		);

	async function send(event: FormEvent) {
		event.preventDefault();
		if (!complete) return;
		setSending(true);
		try {
			await api.answerCard(card.id, card.reading_id, answers as GivenAnswer[]);
			setRefusal(null);
			toast.success(`Answers sent; #${card.number} is queued`);
		} catch (error) {
			setRefusal(describe(error));
		} finally {
			setSending(false);
		}
		await reload();
		askNavAgain();
	}

	return (
		<Card
			title={card.title}
			attention
			aside={<Status tone="needs-you">Needs your answers</Status>}
			className={styles.item}
		>
			<form className={styles.body} onSubmit={send}>
				<p className={styles.where}>
					<span>
						{card.owner}/{card.project_name}
					</span>{" "}
					<span>#{card.number}</span>
				</p>
				{card.feature === null ? null : (
					<p className={styles.text}>
						<span className={styles.prefix}>Card of</span>{" "}
						<span>
							#{card.feature.number} {card.feature.title}
						</span>
					</p>
				)}
				<p className={styles.text}>
					Its reading found choices the card does not settle. Nothing is built
					or posted until you answer them; your answers are posted on its issue
					as your comment.
				</p>
				{card.questions.map((question, at) => (
					<Question
						// A question has no id of its own, and its place is its
						// identity: the answers are sent in this order.
						// biome-ignore lint/suspicious/noArrayIndexKey: see above
						key={at}
						question={question}
						draft={drafts[at] ?? { accepted: false, text: "" }}
						change={(draft) => change(at, draft)}
					/>
				))}
				{refusal === null ? null : (
					<p className={styles.refusal} role="alert">
						{refusal}
					</p>
				)}
				<div className={styles.actions}>
					<Button
						type="submit"
						disabled={!linked || !complete || sending}
						aria-describedby={line === null ? undefined : lineId}
					>
						Send answers
					</Button>
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
			</form>
		</Card>
	);
}

/** One question: what it asks, the recommendation and why, "Accept", and
 * the box for another answer. An answer is one or the other: accepting sets
 * what was typed aside, and taking the acceptance back shows it again, while
 * typing in the box takes the acceptance back. */
function Question({
	question,
	draft,
	change,
}: {
	question: QuestionCardItem["questions"][number];
	draft: Draft;
	change: (draft: Draft) => void;
}) {
	const legendId = useId();
	return (
		<fieldset className={styles.question} aria-labelledby={legendId}>
			<legend id={legendId} className={styles.asked}>
				<Inline text={question.question} />
			</legend>
			<p className={styles.text}>
				<span className={styles.prefix}>Recommended</span>{" "}
				<Inline text={question.recommendation} />
			</p>
			{question.why.trim() === "" ? null : (
				<p className={styles.text}>
					<span className={styles.prefix}>Because</span>{" "}
					<Inline text={question.why} />
				</p>
			)}
			<div className={styles.actions}>
				<Button
					kind={draft.accepted ? "secondary" : "ghost"}
					aria-pressed={draft.accepted}
					onClick={() =>
						change({ accepted: !draft.accepted, text: draft.text })
					}
				>
					{draft.accepted ? "Accepted" : "Accept"}
				</Button>
			</div>
			<TextArea
				label="Or your own answer"
				value={draft.accepted ? "" : draft.text}
				rows={2}
				maxLength={4000}
				onChange={(event) =>
					change({ accepted: false, text: event.target.value })
				}
			/>
		</fieldset>
	);
}
