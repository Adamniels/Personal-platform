// The one frame every kind of item on "Waiting for you" is shown in, beside the queue: a mono line
// of where it is, its status on the right, its title as the hero label, one paragraph of what it
// is, the kind's tiles and blocks, and its actions last over a line. The panel takes the accent
// border when the item needs the owner. See "What waits" in devflow/README.md.
import { type StatusTone, VerdictTile, VerdictTiles } from "@platform/ui";
import {
	Fragment,
	type MouseEvent,
	type ReactElement,
	type ReactNode,
	useId,
} from "react";
import { navigate, plainClick } from "./address";
import { verdictWord } from "./card";
import styles from "./detail.module.css";
import { ArrowUpRightIcon } from "./icons";

export type DetailProps = {
	/** The item's title, its hero label and the article's name. */
	title: string;
	/** Where it is: the repository, then the kind and its number, then the
	 * commit where there is one, joined with " · " in the mono font. */
	where: string[];
	/** Its status label, on the right of the where line. */
	status: ReactElement;
	/** Whether it needs the owner, which gives the panel the accent border;
	 * otherwise, stopped or sent back, it keeps --border. */
	needsYou: boolean;
	/** One paragraph of what it is, in body large. */
	lede?: ReactNode;
	/** The tiles and the blocks. */
	children?: ReactNode;
	/** The actions, last, over a 1px line; none when it acts nowhere. */
	actions?: ReactNode;
};

export function Detail({
	title,
	where,
	status,
	needsYou,
	lede,
	children,
	actions,
}: DetailProps) {
	const titleId = useId();
	return (
		<article
			aria-labelledby={titleId}
			className={needsYou ? `${styles.panel} ${styles.needsYou}` : styles.panel}
		>
			<header className={styles.header}>
				<p className={styles.where}>
					{where.map((part, index) => (
						// A part may repeat, such as a number; its place is its identity.
						// biome-ignore lint/suspicious/noArrayIndexKey: see above
						<Fragment key={index}>
							{index === 0 ? null : " · "}
							<span>{part}</span>
						</Fragment>
					))}
				</p>
				{status}
				{needsYou ? <span className={styles.hidden}>Needs you</span> : null}
			</header>
			<h2 id={titleId} className={styles.title}>
				{title}
			</h2>
			{lede === undefined || lede === null ? null : (
				<p className={styles.lede}>{lede}</p>
			)}
			{children}
			{actions === undefined || actions === null ? null : (
				<div className={styles.actions}>{actions}</div>
			)}
		</article>
	);
}

/** One tile of "At a glance". */
export type Tile = {
	label: string;
	tone: StatusTone;
	status: string;
	line?: ReactNode;
};

/** The tiles, "At a glance", whether the item is safe to act on. */
export function Tiles({ tiles }: { tiles: Tile[] }) {
	return (
		<VerdictTiles aria-label="At a glance">
			{tiles.map((tile) => (
				<VerdictTile
					key={tile.label}
					label={tile.label}
					tone={tile.tone}
					status={tile.status}
					line={tile.line}
				/>
			))}
		</VerdictTiles>
	);
}

/** A page of Dev flow's own as an inset row that links to it, the arrow on
 * the right: "Open the feature", "See its output on the project's page". */
export function ArrowLink({
	href,
	children,
}: {
	href: string;
	children: string;
}) {
	return (
		<a
			className={styles.arrow}
			href={href}
			onClick={(event: MouseEvent<HTMLAnchorElement>) => {
				if (plainClick(event)) {
					event.preventDefault();
					navigate(href);
				}
			}}
		>
			<span>{children}</span>
			<span className={styles.circle} aria-hidden="true">
				<ArrowUpRightIcon size={18} />
			</span>
		</a>
	);
}

/** The line beside the actions, on the right, in the mono font: what the
 * primary does, such as "Merging confirms the 2 decisions". */
export function ActionNote({ children }: { children: ReactNode }) {
	return <p className={styles.note}>{children}</p>;
}

/** "1 decision", "2 decisions". */
export const count = (n: number, word: string) =>
	`${n} ${word}${n === 1 ? "" : "s"}`;

/** A commit as the pages show it, its first seven characters. */
export const short = (commit: string) => commit.slice(0, 7);

/** "The review": the reviewer's verdict on a commit and how many of its
 * findings block, or `none` when there is no review. */
export function reviewTile(
	review: { verdict: string; commit: string; blocking: number } | null,
	none: string,
): Tile {
	if (review === null)
		return {
			label: "The review",
			tone: "idle",
			status: "Not reviewed",
			line: none,
		};
	const { word, tone } = verdictWord(review.verdict);
	return {
		label: "The review",
		tone,
		status: word,
		line: `on ${short(review.commit)} · ${review.blocking} blocking`,
	};
}

/** "Decisions": how many the agent marked for the owner, and how many more
 * it made that it did not need him for. */
export function decisionsTile(forYou: number, others: number): Tile {
	return {
		label: "Decisions",
		tone: forYou > 0 ? "needs-you" : "done",
		status: forYou > 0 ? `${forYou} for you` : "None for you",
		line:
			others > 0
				? `+${others} it did not need you for`
				: "none it decided alone",
	};
}

/** A link to a page of Dev flow's own, followed without a reload. */
export function PageLink({
	href,
	className,
	children,
}: {
	href: string;
	className?: string;
	children: ReactNode;
}) {
	return (
		<a
			className={className}
			href={href}
			onClick={(event: MouseEvent<HTMLAnchorElement>) => {
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
