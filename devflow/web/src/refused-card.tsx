// A card of Dev flow's own refused as too thin to build, as "Waiting for you" shows it under
// Refused: what is missing, each a line of its own, and its issue on GitHub, where the owner's edit
// of it or comment on it reads it again. Nothing is done from the page. Its title and what is
// missing came from the code host or the reader, and are shown as text. See "Reading a card" under
// "Jobs" in devflow/README.md.
import { Card, Status } from "@platform/ui";
import { useId } from "react";
import { Inline } from "./format";
import type { RefusedCardItem } from "./waiting";
import styles from "./waiting.module.css";

const DATE_TIME = new Intl.DateTimeFormat("en-GB", {
	dateStyle: "medium",
	timeStyle: "short",
});

export const READ_AGAIN =
	"Edit the card or comment on it on GitHub, and it is read again.";

export type RefusedCardProps = {
	card: RefusedCardItem;
	/** Whether a link is safe to make of the code host's address. */
	linkable: (address: string) => boolean;
};

/** One refused card: what it lacks, and its issue on GitHub. */
export function RefusedCard({ card, linkable }: RefusedCardProps) {
	const missingId = useId();
	return (
		<Card
			title={card.title}
			aside={<Status tone="failed">Refused</Status>}
			className={styles.item}
			footer={
				<div className={styles.footer}>
					<div className={styles.actions}>
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
				</div>
			}
		>
			<div className={styles.body}>
				<p className={styles.where}>
					<span>
						{card.owner}/{card.project_name}
					</span>{" "}
					<span>#{card.number}</span>
				</p>
				<div className={styles.block}>
					<h3 id={missingId} className={styles.label}>
						What is missing
					</h3>
					<ul className={styles.missing} aria-labelledby={missingId}>
						{card.missing.map((line, index) => (
							// The reader's lines may repeat, so each is kept by its place.
							// biome-ignore lint/suspicious/noArrayIndexKey: a line's place is its identity
							<li key={index}>
								<Inline text={line} />
							</li>
						))}
					</ul>
				</div>
				<p className={styles.text}>
					Read as too thin to build{" "}
					<time dateTime={card.refused_at}>
						{DATE_TIME.format(new Date(card.refused_at))}
					</time>
					. Nothing is built from it as it is.
				</p>
				<p className={styles.text}>{READ_AGAIN}</p>
			</div>
		</Card>
	);
}
