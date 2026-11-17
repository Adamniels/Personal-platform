// A project's Cards panel, under its Settings: every card of the project as its last read found
// it, newest change first, each with its number, linked to the issue on GitHub, its title, its
// state and why it is there. Read from what Dev flow stored, never from GitHub. A card's own page
// comes with a later part. See "Cards" in devflow/README.md.
import { Card as Panel, Status, type StatusTone } from "@platform/ui";
import { useEffect, useState } from "react";
import type { components } from "./api/schema";
import styles from "./cards.module.css";
import { linkable } from "./waiting";

export type Card = components["schemas"]["CardView"];

export const NOT_READ = "The cards could not be read. Try again.";
export const NO_CARDS =
	"No cards yet. An open issue you wrote with the pick-up label becomes one.";

/** How the panel reads a project's cards, Dev flow's API in the page and a
 * test's own in a test. */
export type CardsApi = {
	loadCards: (id: string) => Promise<{ cards: Card[] }>;
};

const STATES: Record<string, { word: string; tone: StatusTone }> = {
	queued: { word: "Queued", tone: "idle" },
	held: { word: "Held", tone: "idle" },
	ignored: { word: "Ignored", tone: "failed" },
	building: { word: "Building", tone: "running" },
	checking: { word: "Checking", tone: "running" },
	reviewing: { word: "Reviewing", tone: "running" },
	fixing: { word: "Fixing", tone: "running" },
	waiting_for_owner: { word: "Waiting for you", tone: "needs-you" },
	done: { word: "Done", tone: "done" },
	stopped: { word: "Stopped", tone: "failed" },
};

/** A card's state as a word and a tone; one the page does not know yet is
 * shown as it is named. */
export function stateWord(state: string): { word: string; tone: StatusTone } {
	return STATES[state] ?? { word: state, tone: "idle" };
}

/** The Cards panel: the list, or why there is none. Nothing is shown in it
 * before the cards are read. */
export function CardsPanel({ id, loadCards }: CardsApi & { id: string }) {
	const [cards, setCards] = useState<Card[]>();
	const [failed, setFailed] = useState(false);

	useEffect(() => {
		let current = true;
		loadCards(id).then(
			(answer) => {
				if (current) setCards(answer.cards);
			},
			() => {
				if (current) setFailed(true);
			},
		);
		return () => {
			current = false;
		};
	}, [id, loadCards]);

	return (
		<Panel title="Cards" className={styles.card}>
			{failed ? (
				<p className={styles.error} role="alert">
					{NOT_READ}
				</p>
			) : cards === undefined ? null : cards.length === 0 ? (
				<p className={styles.muted}>{NO_CARDS}</p>
			) : (
				<ul className={styles.cards} aria-label="Cards">
					{cards.map((card) => (
						<CardRow key={card.number} card={card} />
					))}
				</ul>
			)}
		</Panel>
	);
}

/** A card as an inset row: its number, linked when the address is https,
 * its title, its state, and its reason under them. */
function CardRow({ card }: { card: Card }) {
	const { word, tone } = stateWord(card.state);
	const number = `#${card.number}`;
	return (
		<li className={styles.row}>
			<span className={styles.head}>
				{linkable(card.web_url) ? (
					<a
						className={styles.number}
						href={card.web_url}
						target="_blank"
						rel="noreferrer"
					>
						{number}
					</a>
				) : (
					<span className={styles.number}>{number}</span>
				)}
				<span className={styles.title}>{card.title}</span>
				<Status tone={tone} className={styles.state}>
					{word}
				</Status>
			</span>
			{card.reason === null ? null : (
				<span className={styles.reason}>{card.reason}</span>
			)}
		</li>
	);
}
