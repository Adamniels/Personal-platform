// The Study tab at /review: how many cards are due in all, Start everything, the tab's one primary
// action, and what is due by deck and by topic, each with its own Start. Counted from one call, the
// queue without a filter, grouped here. See "Studying" in review/README.md.
import { Badge, Card, EmptyState } from "@platform/ui";
import { useId } from "react";
import { AppLink } from "./app-link";
import { nextDueSentence } from "./intervals";
import { cards, type DueGroup, groupDue, QUEUE_MAX, useDue } from "./study";
import styles from "./study.module.css";

export function StudyStart() {
	const due = useDue();

	if (due.isPending) return <p className={styles.note}>Loading what is due…</p>;
	if (due.isError) {
		return <p className={styles.note}>What is due could not be loaded.</p>;
	}

	const queue = due.data;
	if (queue.total === 0) {
		return <NothingDue nextDue={queue.next_due} asOf={queue.as_of} />;
	}

	const { decks, topics } = groupDue(queue.cards);
	return (
		<div className={styles.start}>
			<div className={styles.summary}>
				<h2>{`${cards(queue.total)} due`}</h2>
				<AppLink kind="primary" href="/review/study">
					Start everything
				</AppLink>
			</div>
			{queue.total > QUEUE_MAX ? (
				<p className={styles.note}>The lists count the first {QUEUE_MAX}.</p>
			) : null}
			<div className={styles.groups}>
				<Groups title="By deck" groups={decks} />
				<Groups title="By topic" groups={topics} />
			</div>
		</div>
	);
}

function Groups({ title, groups }: { title: string; groups: DueGroup[] }) {
	return (
		<Card title={title}>
			<ul className={styles.list} aria-label={title}>
				{groups.map((group) => (
					<Group key={group.key} group={group} />
				))}
			</ul>
		</Card>
	);
}

function Group({ group }: { group: DueGroup }) {
	const nameId = useId();
	return (
		<li className={styles.row}>
			<span id={nameId} className={styles.name}>
				{group.name}
			</span>
			<Badge>{`${group.count} due`}</Badge>
			<AppLink
				kind="secondary"
				href={group.path}
				aria-describedby={nameId}
				className={styles.rowAction}
			>
				Start
			</AppLink>
		</li>
	);
}

/** Nothing is due: when the next card is, and the way to Cards. */
export function NothingDue({
	nextDue,
	asOf,
}: {
	nextDue: string | null;
	asOf: string;
}) {
	return (
		<EmptyState
			title="Nothing due"
			detail={
				nextDue === null
					? "You have no cards yet. Add some in Cards."
					: nextDueSentence(nextDue, asOf)
			}
			action={<AppLink href="/review/cards">Go to Cards</AppLink>}
		/>
	);
}
