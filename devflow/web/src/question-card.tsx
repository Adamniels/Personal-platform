// A card of Dev flow's own whose reading found questions, as "Waiting for you" shows it under
// "Questions before starting", in the detail's frame: one inset per question, numbered, with the
// reader's recommendation and why, "Accept" and "Write another answer", which opens a box for his
// own; an answered one says Accepted or Your answer, with Change, which opens it again. One "Send
// answers", the primary, posts them as the owner on its issue and queues it, and stays disabled
// until each is answered. What he typed is kept until a send succeeds. Its title and every
// question came from the code host or the reader, and are shown as text. See "Reading a card"
// under "Jobs" in devflow/README.md.
import { Button, Status, TextArea, useToast } from "@platform/ui";
import { useId, useState } from "react";
import {
	type ActionsApi,
	describe,
	EXPIRED,
	type GivenAnswer,
	type LinkState,
	UNLINKED,
} from "./actions";
import { ActionNote, count, Detail } from "./detail";
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

/** One question's answer as the page holds it: open, the recommendation
 * accepted, or the owner's own being written; what he typed is kept
 * whichever it is, until a send succeeds. */
type Draft = { mode: "open" | "accepted" | "writing"; text: string };

const OPEN: Draft = { mode: "open", text: "" };

/** The answer to send for a draft, or undefined while it has none. */
function given(draft: Draft): GivenAnswer | undefined {
	if (draft.mode === "accepted") return { accept: true };
	const text = draft.text.trim();
	return draft.mode === "writing" && text !== "" ? { text } : undefined;
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
		card.questions.map(() => OPEN),
	);
	const [sending, setSending] = useState(false);
	const [refusal, setRefusal] = useState<string | null>(null);
	const linked = link === "linked";
	// Nothing to say while the link's state is still being read.
	const line =
		link === "none" ? UNLINKED : link === "needs_relink" ? EXPIRED : null;
	const answers = drafts.map(given);
	const left = answers.filter((answer) => answer === undefined).length;

	const change = (at: number, draft: Draft) =>
		setDrafts((before) =>
			before.map((each, index) => (index === at ? draft : each)),
		);

	async function send() {
		if (left > 0) return;
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
		<Detail
			title={card.title}
			where={[
				`${card.owner}/${card.project_name}`,
				`card #${card.number}`,
				...(card.feature === null ? [] : [`feature #${card.feature.number}`]),
			]}
			status={<Status tone="needs-you">Needs your answers</Status>}
			needsYou
			lede="Its reading found choices the card does not settle. Nothing is built or posted until you answer them; your answers are posted on its issue as your comment."
			actions={
				<>
					{refusal === null ? null : (
						<p className={styles.refusal} role="alert">
							{refusal}
						</p>
					)}
					<div className={styles.actions}>
						<Button
							kind="primary"
							disabled={!linked || left > 0 || sending}
							aria-describedby={line === null ? undefined : lineId}
							onClick={send}
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
						<ActionNote>
							{count(left, "question")} left · sent as one comment
						</ActionNote>
					</div>
					{line === null ? null : (
						<p id={lineId} className={styles.why}>
							{line}
						</p>
					)}
				</>
			}
		>
			{card.questions.map((question, at) => (
				<Question
					// A question has no id of its own, and its place is its
					// identity: the answers are sent in this order.
					// biome-ignore lint/suspicious/noArrayIndexKey: see above
					key={at}
					number={at + 1}
					question={question}
					draft={drafts[at] ?? OPEN}
					change={(draft) => change(at, draft)}
				/>
			))}
		</Detail>
	);
}

/** One question on its inset: its number and its state, what it asks, the
 * recommendation and why, and what can be done with it now. */
function Question({
	number,
	question,
	draft,
	change,
}: {
	number: number;
	question: QuestionCardItem["questions"][number];
	draft: Draft;
	change: (draft: Draft) => void;
}) {
	const askedId = useId();
	const answer = given(draft);
	return (
		<section role="group" aria-labelledby={askedId} className={styles.question}>
			<div className={styles.questionHead}>
				<span className={styles.number}>{String(number).padStart(2, "0")}</span>
				{answer === undefined ? (
					<Status tone="needs-you">Open</Status>
				) : "accept" in answer ? (
					<Status tone="done">Accepted</Status>
				) : (
					<Status tone="running">Your answer</Status>
				)}
			</div>
			<p id={askedId} className={styles.asked}>
				<Inline text={question.question} />
			</p>
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
			{draft.mode === "writing" ? (
				<TextArea
					label="Another answer"
					value={draft.text}
					rows={2}
					maxLength={4000}
					onChange={(event) =>
						change({ mode: "writing", text: event.target.value })
					}
				/>
			) : null}
			<div className={styles.actions}>
				{draft.mode === "open" ? (
					<>
						<Button onClick={() => change({ ...draft, mode: "accepted" })}>
							Accept
						</Button>
						<Button
							kind="ghost"
							onClick={() => change({ ...draft, mode: "writing" })}
						>
							Write another answer
						</Button>
					</>
				) : (
					<Button
						kind="ghost"
						onClick={() => change({ ...draft, mode: "open" })}
					>
						Change
					</Button>
				)}
			</div>
		</section>
	);
}
