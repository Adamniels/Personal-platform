// A project's Cards panel, under its Settings: the project's features as its last read found
// them, each with its number, linked to its parent issue on GitHub, its title, linked to the
// feature's page, its state and why it is there, and its cards in their order; then every card on
// its own, newest change first. A card shows its number, linked to the issue on GitHub, its title,
// a link to the card's own page, its state and why it is there. Read from what Dev flow stored, never from GitHub. See "Cards"
// and "Features" in devflow/README.md.
import { Card as Panel, Status, type StatusTone } from "@platform/ui";
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

/** A project's features and its cards on their own, as the cards route
 * answers them. */
export type CardList = { cards: Card[]; features: Feature[] };

/** How the page reads a project's cards, Dev flow's API in the page and a
 * test's own in a test. */
export type CardsApi = {
	loadCards: (id: string) => Promise<CardList>;
};

/** The groups a project's cards are counted in, by their states: the same
 * four the projects list's summary counts, `ProjectSummaries.Groups` in the
 * service, an ignored card in none. See "Projects" in
 * devflow/docs/devflow-architecture.md. */
export const CARD_GROUPS = {
	in_progress: ["reading", "building", "checking", "reviewing", "fixing"],
	needs_you: ["needs_answers", "waiting_for_owner", "stopped", "refused"],
	queued: ["queued", "held"],
	done: ["done"],
} as const satisfies Record<string, readonly string[]>;

export type CardGroup = keyof typeof CARD_GROUPS;
export type CardCounts = Record<CardGroup, number>;

const groupOf = (state: string): CardGroup | undefined =>
	(Object.keys(CARD_GROUPS) as CardGroup[]).find((group) =>
		(CARD_GROUPS[group] as readonly string[]).includes(state),
	);

/** Every card of the project, its features' and those on their own,
 * counted by group. */
export function countCards({ cards, features }: CardList): CardCounts {
	const counts: CardCounts = {
		in_progress: 0,
		needs_you: 0,
		queued: 0,
		done: 0,
	};
	for (const card of [...features.flatMap((f) => f.cards), ...cards]) {
		const group = groupOf(card.state);
		if (group !== undefined) counts[group] += 1;
	}
	return counts;
}

/** The counts as the page's one summary line, "9 cards: 2 being built, 2
 * need you, 2 queued, 3 done.", leaving out a group with none. */
export function summaryLine(counts: CardCounts): string {
	const total =
		counts.in_progress + counts.needs_you + counts.queued + counts.done;
	if (total === 0) return "No cards yet.";
	const parts = [
		counts.in_progress > 0 ? `${counts.in_progress} being built` : null,
		counts.needs_you > 0
			? `${counts.needs_you} ${counts.needs_you === 1 ? "needs" : "need"} you`
			: null,
		counts.queued > 0 ? `${counts.queued} queued` : null,
		counts.done > 0 ? `${counts.done} done` : null,
	].filter((part) => part !== null);
	return `${total} ${total === 1 ? "card" : "cards"}: ${parts.join(", ")}.`;
}

const STATES: Record<string, { word: string; tone: StatusTone }> = {
	queued: { word: "Queued", tone: "idle" },
	reading: { word: "Reading", tone: "running" },
	needs_answers: { word: "Needs your answers", tone: "needs-you" },
	held: { word: "Held", tone: "idle" },
	ignored: { word: "Ignored", tone: "failed" },
	building: { word: "Building", tone: "running" },
	bringing_in: { word: "Bringing in the default branch", tone: "running" },
	resolving: { word: "Resolving a conflict", tone: "running" },
	checking: { word: "Checking", tone: "running" },
	reviewing: { word: "Reviewing", tone: "running" },
	fixing: { word: "Fixing", tone: "running" },
	waiting_for_owner: { word: "Waiting for you", tone: "needs-you" },
	ready: { word: "Ready for you", tone: "needs-you" },
	done: { word: "Done", tone: "done" },
	merged: { word: "Merged", tone: "done" },
	closed: { word: "Closed", tone: "idle" },
	stopped: { word: "Stopped", tone: "failed" },
	refused: { word: "Too thin to build", tone: "failed" },
};

/** A card's or a feature's state as a word and a tone; one the page does not
 * know yet is shown as it is named. */
export function stateWord(state: string): { word: string; tone: StatusTone } {
	return STATES[state] ?? { word: state, tone: "idle" };
}

/** The Cards panel: the features, then the cards on their own, or why there
 * is none, from the cards the page read: none yet before they answer, and
 * null when they could not be read. */
export function CardsPanel({
	id,
	list,
}: {
	id: string;
	list?: CardList | null;
}) {
	return (
		<Panel title="Cards" className={styles.card}>
			{list === null ? (
				<p className={styles.error} role="alert">
					{NOT_READ}
				</p>
			) : list === undefined ? null : list.cards.length === 0 &&
				list.features.length === 0 ? (
				<p className={styles.muted}>{NO_CARDS}</p>
			) : (
				<div className={styles.lists}>
					{list.features.length === 0 ? null : (
						<ul className={styles.cards} aria-label="Features">
							{list.features.map((feature) => (
								<FeatureRow
									key={feature.number}
									project={id}
									feature={feature}
								/>
							))}
						</ul>
					)}
					{list.cards.length === 0 ? null : (
						<ul className={styles.cards} aria-label="Cards">
							{list.cards.map((card) => (
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
