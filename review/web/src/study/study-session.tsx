// A study session at /review/study, `?deck=<id>` or `?topic=<slug>`, or neither for everything: one
// card at a time, its front, then its back and the four grades, each saying when it would bring the
// card back, until nothing is due. The queue is fetched at the start, again after every 20 grades,
// and again when it runs out, so a card graded Again that is due again within the session comes
// back in it. See "Studying" in review/README.md.
import { Button, Card, Chip, useToast } from "@platform/ui";
import { useCallback, useEffect, useRef, useState } from "react";
import { isConflict, useClient } from "../providers";
import { AppLink } from "./app-link";
import { interval, nextDueSentence } from "./intervals";
import {
	cards,
	type DueCard,
	GRADES,
	type Grade,
	type Queue,
	useGrade,
} from "./study";
import styles from "./study.module.css";
import { NothingDue } from "./study-start";

/** How many grades go by before the queue is read again. */
const REFETCH_AFTER = 20;

type State =
	/** Reading the queue. `total` is the count at the start, null before the
	 * first read. */
	| { phase: "loading"; total: number | null; reviewed: number }
	| { phase: "failed"; total: number | null; reviewed: number }
	| {
			phase: "studying";
			/** The cards still to show from the last read, the current first. */
			cards: DueCard[];
			/** The queue's own time, which every preview is written from. */
			asOf: string;
			total: number;
			reviewed: number;
			/** Grades since the queue was last read. */
			sinceRead: number;
			/** Whether the current card's answer shows. */
			shown: boolean;
	  }
	| {
			phase: "done";
			/** Whether a card was due at the start; if not, nothing was. */
			started: boolean;
			reviewed: number;
			nextDue: string | null;
			asOf: string;
	  };

export function StudySession({
	deck,
	topic,
}: {
	deck: string | undefined;
	topic: string | undefined;
}) {
	const client = useClient();
	const toast = useToast();
	const grade = useGrade();
	const [state, setState] = useState<State>({
		phase: "loading",
		total: null,
		reviewed: 0,
	});
	// Set while a grade is in flight, before React renders the buttons
	// disabled, so a second key press in the same moment sends nothing.
	const grading = useRef(false);
	const [inFlight, setInFlight] = useState(false);
	// Each read of the queue is numbered, so one that answers after a later
	// one, or after the session has gone, is dropped.
	const reads = useRef(0);
	const showButton = useRef<HTMLButtonElement>(null);
	const answer = useRef<HTMLDivElement>(null);

	const read = useCallback(
		async (total: number | null, reviewed: number) => {
			const which = ++reads.current;
			setState({ phase: "loading", total, reviewed });
			let queue: Queue;
			try {
				queue = await client.get("/queue", { query: { deck, topic } });
			} catch {
				if (which === reads.current) {
					setState({ phase: "failed", total, reviewed });
				}
				return;
			}
			if (which !== reads.current) return;
			setState(
				queue.cards.length === 0
					? {
							phase: "done",
							started: total !== null,
							reviewed,
							nextDue: queue.next_due,
							asOf: queue.as_of,
						}
					: {
							phase: "studying",
							cards: queue.cards,
							asOf: queue.as_of,
							total: total ?? queue.total,
							reviewed,
							sinceRead: 0,
							shown: false,
						},
			);
		},
		[client, deck, topic],
	);

	useEffect(() => {
		void read(null, 0);
		return () => {
			reads.current += 1;
		};
	}, [read]);

	const current = state.phase === "studying" ? state.cards[0] : undefined;
	const shown = state.phase === "studying" && state.shown;
	const currentId = current?.id;

	// Focus follows the session: Show answer for a new card, so Space and
	// Enter work from it, then the answer once it shows, so it is read out.
	useEffect(() => {
		if (currentId === undefined) return;
		if (shown) answer.current?.focus();
		else showButton.current?.focus();
	}, [currentId, shown]);

	function show() {
		setState((now) =>
			now.phase === "studying" ? { ...now, shown: true } : now,
		);
	}

	async function gradeWith(chosen: Grade) {
		if (state.phase !== "studying" || !state.shown || grading.current) return;
		const [card, ...rest] = state.cards;
		if (!card) return;
		grading.current = true;
		setInFlight(true);
		try {
			await grade.mutateAsync({ card, grade: chosen });
		} catch (failure) {
			// The card stays, so the grade can be tried again; the toast has
			// been raised for anything but a 409.
			if (isConflict(failure)) {
				toast.error("This card changed elsewhere");
				await read(state.total, state.reviewed);
			}
			return;
		} finally {
			grading.current = false;
			setInFlight(false);
		}
		const reviewed = state.reviewed + 1;
		const sinceRead = state.sinceRead + 1;
		if (rest.length === 0 || sinceRead >= REFETCH_AFTER) {
			await read(state.total, reviewed);
			return;
		}
		setState({ ...state, cards: rest, reviewed, sinceRead, shown: false });
	}

	// The keys, read from the window, through a ref, so the listener added
	// once always sees the session as it is now.
	const onKey = useRef<(event: KeyboardEvent) => void>(() => undefined);
	onKey.current = (event) => {
		if (state.phase !== "studying") return;
		if (event.altKey || event.ctrlKey || event.metaKey) return;
		const target = event.target instanceof Element ? event.target : null;
		if (target?.closest("input, textarea, select, [contenteditable]")) return;
		const chosen = GRADES.find((each) => String(each.rating) === event.key);
		if (event.key !== " " && !chosen) return;
		// A key held down acts once.
		if (event.repeat) {
			event.preventDefault();
			return;
		}
		if (event.key === " ") {
			if (state.shown) return;
			// Space on another control, a tab or a link, is that control's own.
			if (target?.closest("a, button") && target !== showButton.current) {
				return;
			}
			event.preventDefault();
			show();
			return;
		}
		if (chosen && state.shown) {
			event.preventDefault();
			void gradeWith(chosen);
		}
	};
	useEffect(() => {
		const listener = (event: KeyboardEvent) => onKey.current(event);
		window.addEventListener("keydown", listener);
		return () => window.removeEventListener("keydown", listener);
	}, []);

	if (state.phase === "loading") {
		return <p className={styles.note}>Loading the cards due…</p>;
	}
	if (state.phase === "failed") {
		return (
			<div className={styles.failed}>
				<p className={styles.note}>The cards due could not be loaded.</p>
				<Button onClick={() => void read(state.total, state.reviewed)}>
					Try again
				</Button>
			</div>
		);
	}
	if (state.phase === "done") {
		if (!state.started) {
			return (
				<div className={styles.state}>
					<NothingDue nextDue={state.nextDue} asOf={state.asOf} />
				</div>
			);
		}
		return (
			<Card
				title="Done for now"
				className={`${styles.state} ${styles.finished}`}
			>
				<div className={styles.done}>
					<p>{`You reviewed ${cards(state.reviewed)}.`}</p>
					<p>
						{state.nextDue === null
							? "No card is coming back yet."
							: nextDueSentence(state.nextDue, state.asOf)}
					</p>
					<p className={styles.links}>
						<AppLink href="/review">Back to Study</AppLink>
						<AppLink href="/review/cards">Go to Cards</AppLink>
					</p>
				</div>
			</Card>
		);
	}

	if (!current) return null;
	return (
		<div className={styles.session}>
			<p className={styles.progress}>
				{`${state.reviewed + 1} of ${state.total}`}
			</p>
			<Card title={current.deck.name}>
				<div className={styles.study}>
					<ul className={styles.topics} aria-label="Topics">
						{current.topics.map((each) => (
							<li key={each.slug}>
								<Chip>{each.name}</Chip>
							</li>
						))}
					</ul>
					<p className={styles.side}>{current.front}</p>
					{state.shown ? (
						<div ref={answer} tabIndex={-1} className={styles.answer}>
							<hr className={styles.divider} />
							<p className={styles.side}>{current.back}</p>
							{current.source ? (
								<a className={styles.source} href={current.source.path}>
									{current.source.title}
								</a>
							) : null}
						</div>
					) : (
						<div>
							<Button ref={showButton} kind="primary" onClick={show}>
								Show answer
							</Button>
						</div>
					)}
				</div>
			</Card>
			{state.shown ? (
				<div className={styles.grades}>
					{GRADES.map((each) => {
						const after = interval(state.asOf, current.previews[each.preview]);
						return (
							<Button
								key={each.rating}
								kind={each.kind}
								className={styles.grade}
								// The name as it reads on desktop, "Good · 4d", whichever
								// way the phone layout stacks the two.
								aria-label={`${each.label} · ${after}`}
								disabled={inFlight}
								onClick={() => void gradeWith(each)}
							>
								{/* Each part its own element, so the phone layout can stack
								 * them; text runs either side of a hidden separator would
								 * join into one. */}
								<span>{each.label}</span>
								<span className={styles.separator}>·</span>
								<span>{after}</span>
							</Button>
						);
					})}
				</div>
			) : null}
			<p className={styles.keys}>Space to show, 1 to 4 to grade</p>
		</div>
	);
}
