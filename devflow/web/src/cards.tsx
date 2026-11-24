// A project's cards, in the cards column of its page: each feature as its last read found it, a
// card of its own with its state, its title, linked to the feature's page, its cards landed of
// its total as steps, and its cards in their order; then the cards on their own, a table newest
// change first. A card shows its number, linked to the issue on GitHub, its title, a link to the
// card's own page, its state and why it is there. Read from what Dev flow stored, never from
// GitHub. See "Cards" and "Features" in devflow/README.md.
import {
	EmptyState,
	Card as Panel,
	Status,
	type StatusTone,
	Table,
	type TableColumn,
	type TableSortDirection,
} from "@platform/ui";
import { useId, useState } from "react";
import { navigate, plainClick } from "./address";
import type { components } from "./api/schema";
import styles from "./cards.module.css";
import { cardPath, featurePath } from "./sections";
import { linkable } from "./waiting";

export type Card = components["schemas"]["CardView"];
export type Feature = components["schemas"]["FeatureView"];

export const NOT_READ = "The cards could not be read. Try again.";
export const NO_CARDS =
	"An open issue you wrote with the pick-up label becomes one, and one with sub-issues a feature.";
/** The table of cards on their own, and its card's label. */
export const ON_THEIR_OWN = "Cards on their own";

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

/** The cards column's cards: each feature as a card of its own, then the
 * cards on their own as a table, or why there is none, from the cards the
 * page read: none yet before they answer, and null when they could not be
 * read. A region named "Cards", apart from the setup and the settings the
 * page may put above it. */
export function CardsPanel({
	id,
	list,
	now,
}: {
	id: string;
	list?: CardList | null;
	/** The time, in milliseconds since the epoch, a card's change is aged by. */
	now: number;
}) {
	return (
		<section className={styles.column} aria-label="Cards">
			{list === null ? (
				<p className={styles.error} role="alert">
					{NOT_READ}
				</p>
			) : list === undefined ? null : list.cards.length === 0 &&
				list.features.length === 0 ? (
				<EmptyState title="No cards yet" detail={NO_CARDS} />
			) : (
				<>
					{list.features.length === 0 ? null : (
						<ul className={styles.features} aria-label="Features">
							{list.features.map((feature) => (
								<li key={feature.number}>
									<FeatureCard project={id} feature={feature} />
								</li>
							))}
						</ul>
					)}
					{list.cards.length === 0 ? null : (
						<CardsTable project={id} cards={list.cards} now={now} />
					)}
				</>
			)}
		</section>
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

/** A link to a page of Dev flow's own, followed without a reload. */
function PageLink({
	href,
	className,
	children,
}: {
	href: string;
	className?: string;
	children: string;
}) {
	return (
		<a
			className={className}
			href={href}
			onClick={(event) => {
				if (plainClick(event)) {
					event.preventDefault();
					navigate(href);
				}
			}}
		>
			{children}
		</a>
	);
}

/** A feature as a card: its label, "Feature #N", with its state; its title
 * as the hero label, a link to the feature's page; a mono line with its
 * cards landed of its total, an ignored card in neither, and its number
 * linked to its parent issue; its reason; the steps, one per card counted,
 * landed, current or still to do; and its cards in their order, each an
 * inset row. Its fix rounds are on its own page. */
export function FeatureCard({
	project,
	feature,
}: {
	project: string;
	feature: Feature;
}) {
	const { word, tone } = stateWord(feature.state);
	const counted = feature.cards.filter((card) => card.state !== "ignored");
	const landed = counted.filter((card) => card.state === "done").length;
	const progress = `${landed} of ${counted.length} landed`;
	// A feature's cards are built one at a time, in order: the first not
	// landed is the current one.
	const current = counted.findIndex((card) => card.state !== "done");
	return (
		<Panel
			title={`Feature #${feature.number}`}
			aside={<Status tone={tone}>{word}</Status>}
			className={styles.feature}
		>
			<div className={styles.body}>
				<div className={styles.lead}>
					<PageLink
						className={styles.hero}
						href={featurePath(project, feature.number)}
					>
						{feature.title}
					</PageLink>
					<p className={styles.progress}>
						<span>{progress}</span>
						<IssueNumber number={feature.number} url={feature.web_url} />
					</p>
				</div>
				{feature.reason === null ? null : (
					<p className={styles.reason}>{feature.reason}</p>
				)}
				{counted.length === 0 ? null : (
					<div className={styles.steps} role="img" aria-label={progress}>
						{counted.map((card, at) => (
							<span
								key={card.number}
								className={styles.step}
								data-step={
									card.state === "done"
										? "landed"
										: at === current
											? "current"
											: "to-do"
								}
							/>
						))}
					</div>
				)}
				<ol className={styles.rows} aria-label={`Cards of #${feature.number}`}>
					{feature.cards.map((card) => (
						<CardRow key={card.number} project={project} card={card} />
					))}
				</ol>
			</div>
		</Panel>
	);
}

/** A card of a feature as an inset row: its number, linked to the issue when
 * the address is https, its title, a link to the card's page, its state, and
 * its reason under them. */
function CardRow({ project, card }: { project: string; card: Card }) {
	const { word, tone } = stateWord(card.state);
	return (
		<li className={styles.row} data-lines={card.reason === null ? 1 : 2}>
			<span className={styles.head}>
				<IssueNumber number={card.number} url={card.web_url} />
				<PageLink
					className={styles.title}
					href={cardPath(project, card.number)}
				>
					{card.title}
				</PageLink>
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

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** How long ago, short, for a table's mono column: "now", "12m", "3h",
 * "2d". */
export function shortAge(ms: number): string {
	if (ms < MINUTE) return "now";
	if (ms < HOUR) return `${Math.floor(ms / MINUTE)}m`;
	if (ms < DAY) return `${Math.floor(ms / HOUR)}h`;
	return `${Math.floor(ms / DAY)}d`;
}

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

/** The cards on their own, a table in a card of its own, newest change first
 * unless Changed is pressed: # and Card, with its reason under its title,
 * and State, priority 1; Changed, priority 3, hidden on phone. */
export function CardsTable({
	project,
	cards,
	now,
}: {
	project: string;
	cards: Card[];
	now: number;
}) {
	const label = useId();
	const [direction, setDirection] = useState<TableSortDirection>("descending");
	const rows = [...cards].sort((a, b) => {
		const newer = Date.parse(b.changed_at) - Date.parse(a.changed_at);
		return direction === "descending" ? newer : -newer;
	});
	const columns: TableColumn<Card>[] = [
		{
			key: "number",
			header: "#",
			cell: (card) => <IssueNumber number={card.number} url={card.web_url} />,
		},
		{
			key: "card",
			header: "Card",
			cell: (card) => (
				<div className={styles.cardCell}>
					<PageLink
						className={styles.title}
						href={cardPath(project, card.number)}
					>
						{card.title}
					</PageLink>
					{card.reason === null ? null : (
						<span className={styles.reason}>{card.reason}</span>
					)}
				</div>
			),
		},
		{
			key: "state",
			header: "State",
			cell: (card) => {
				const { word, tone } = stateWord(card.state);
				return <Status tone={tone}>{word}</Status>;
			},
		},
		{
			key: "changed",
			header: "Changed",
			priority: 3,
			numeric: true,
			sortable: true,
			cell: (card) => (
				<time
					dateTime={card.changed_at}
					title={DATE_TIME.format(new Date(card.changed_at))}
				>
					{shortAge(Math.max(0, now - Date.parse(card.changed_at)))}
				</time>
			),
		},
	];
	return (
		<article className={styles.tableCard} aria-labelledby={label}>
			<div className={styles.tableHead}>
				<h2 id={label} className={styles.label}>
					{ON_THEIR_OWN}
				</h2>
				<span className={styles.order}>
					{direction === "descending"
						? "newest change first"
						: "oldest change first"}
				</span>
			</div>
			<Table
				label={ON_THEIR_OWN}
				columns={columns}
				rows={rows}
				rowKey={(card) => String(card.number)}
				sort={{ key: "changed", direction }}
				onSort={(sort) => setDirection(sort.direction)}
				className={styles.table}
			/>
		</article>
	);
}
