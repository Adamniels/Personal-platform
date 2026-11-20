// A project's Cards panel, under its Settings: the project's features as its last read found
// them, each with its number, linked to its parent issue on GitHub, its title, linked to the
// feature's page, its state and why it is there, and its cards in their order; then every card on
// its own, newest change first. A card shows its number, linked to the issue on GitHub, its title,
// a link to the card's own page, its state and why it is there. Read from what Dev flow stored, never from GitHub. See "Cards"
// and "Features" in devflow/README.md.
import { Card as Panel, Status, type StatusTone } from "@platform/ui";
import { useEffect, useState } from "react";
import { navigate, plainClick } from "./address";
import type { components } from "./api/schema";
import styles from "./cards.module.css";
import { cardPath, featurePath } from "./sections";
import { linkable } from "./waiting";

export type Card = components["schemas"]["CardView"];
export type Feature = components["schemas"]["FeatureView"];

export const NOT_READ = "The cards could not be read. Try again.";
export const NO_CARDS =
	"No cards yet. An open issue you wrote with the pick-up label becomes one, and one with sub-issues a feature.";

/** How the panel reads a project's cards, Dev flow's API in the page and a
 * test's own in a test. */
export type CardsApi = {
	loadCards: (id: string) => Promise<{ cards: Card[]; features: Feature[] }>;
};

const STATES: Record<string, { word: string; tone: StatusTone }> = {
	queued: { word: "Queued", tone: "idle" },
	held: { word: "Held", tone: "idle" },
	ignored: { word: "Ignored", tone: "failed" },
	building: { word: "Building", tone: "running" },
	bringing_in: { word: "Bringing in the default branch", tone: "running" },
	resolving: { word: "Resolving a conflict", tone: "running" },
	checking: { word: "Checking", tone: "running" },
	reviewing: { word: "Reviewing", tone: "running" },
	fixing: { word: "Fixing", tone: "running" },
	waiting_for_owner: { word: "Waiting for you", tone: "needs-you" },
	done: { word: "Done", tone: "done" },
	stopped: { word: "Stopped", tone: "failed" },
};

/** A card's or a feature's state as a word and a tone; one the page does not
 * know yet is shown as it is named. */
export function stateWord(state: string): { word: string; tone: StatusTone } {
	return STATES[state] ?? { word: state, tone: "idle" };
}

/** The Cards panel: the features, then the cards on their own, or why there
 * is none. Nothing is shown in it before the cards are read. */
export function CardsPanel({ id, loadCards }: CardsApi & { id: string }) {
	const [answer, setAnswer] = useState<{
		cards: Card[];
		features: Feature[];
	}>();
	const [failed, setFailed] = useState(false);

	useEffect(() => {
		let current = true;
		loadCards(id).then(
			(loaded) => {
				if (current) setAnswer(loaded);
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
			) : answer === undefined ? null : answer.cards.length === 0 &&
				answer.features.length === 0 ? (
				<p className={styles.muted}>{NO_CARDS}</p>
			) : (
				<div className={styles.lists}>
					{answer.features.length === 0 ? null : (
						<ul className={styles.cards} aria-label="Features">
							{answer.features.map((feature) => (
								<FeatureRow
									key={feature.number}
									project={id}
									feature={feature}
								/>
							))}
						</ul>
					)}
					{answer.cards.length === 0 ? null : (
						<ul className={styles.cards} aria-label="Cards">
							{answer.cards.map((card) => (
								<CardRow key={card.number} project={id} card={card} />
							))}
						</ul>
					)}
				</div>
			)}
		</Panel>
	);
}

/** An issue's number, linked to it on the code host when the address is
 * https, and plain text otherwise. */
function IssueNumber({ number, url }: { number: number; url: string }) {
	const text = `#${number}`;
	return linkable(url) ? (
		<a className={styles.number} href={url} target="_blank" rel="noreferrer">
			{text}
		</a>
	) : (
		<span className={styles.number}>{text}</span>
	);
}

/** A feature as an inset row: its number, linked to its parent issue, its
 * title, linked to the feature's page, its state, its reason under them, and
 * then its cards in their order, each a row of its own. */
function FeatureRow({
	project,
	feature,
}: {
	project: string;
	feature: Feature;
}) {
	const { word, tone } = stateWord(feature.state);
	const page = featurePath(project, feature.number);
	return (
		<li className={styles.row}>
			<div className={styles.head}>
				<IssueNumber number={feature.number} url={feature.web_url} />
				<a
					className={styles.title}
					href={page}
					onClick={(event) => {
						if (plainClick(event)) {
							event.preventDefault();
							navigate(page);
						}
					}}
				>
					{feature.title}
				</a>
				<Status tone={tone} className={styles.state}>
					{word}
				</Status>
			</div>
			{feature.reason === null ? null : (
				<span className={styles.reason}>{feature.reason}</span>
			)}
			<ol
				className={styles.featureCards}
				aria-label={`Cards of #${feature.number}`}
			>
				{feature.cards.map((card) => (
					<CardRow key={card.number} project={project} card={card} inFeature />
				))}
			</ol>
		</li>
	);
}

/** A card as an inset row: its number, linked to the issue when the address
 * is https, its title, a link to the card's page, its state, and its reason
 * under them. Inside a feature it sits on the card's own surface, a step up
 * from the feature's inset. */
function CardRow({
	project,
	card,
	inFeature = false,
}: {
	project: string;
	card: Card;
	inFeature?: boolean;
}) {
	const { word, tone } = stateWord(card.state);
	const page = cardPath(project, card.number);
	return (
		<li className={inFeature ? styles.featureCard : styles.row}>
			<span className={styles.head}>
				<IssueNumber number={card.number} url={card.web_url} />
				<a
					className={styles.title}
					href={page}
					onClick={(event) => {
						if (plainClick(event)) {
							event.preventDefault();
							navigate(page);
						}
					}}
				>
					{card.title}
				</a>
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
