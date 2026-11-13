// An answer as a feature would receive it, shown so it can be checked: the conflicts first, when
// there are any, then the items in the order the brain returned them, each with why it is believed,
// and the raw response under it. Both items of a conflict always stay in the list, as "Conflicting
// decisions before consolidation" in Brain/docs/event-model.md requires.
import {
	Button,
	Card,
	Status,
	Timeline,
	TimelineEntry,
	useToast,
} from "@platform/ui";
import { useId, useState } from "react";
import type { components } from "../api/schema";
import { stateOf } from "../everything/state";
import {
	kindWord,
	originWords,
	provenanceWord,
	storeWord,
	verbWords,
} from "../everything/words";
import { writtenWhen } from "../history/when";
import { SubjectBadges } from "../subjects/subject-fields";
import styles from "./answer.module.css";
import {
	type AnswerItem,
	firstPlaces,
	type Group,
	groupsOf,
	itemsById,
} from "./groups";
import type { Asked } from "./questions";
import { anchorOf, REASONS, supportLine } from "./words";

type Conflict = components["schemas"]["Conflict"];

export function AnswerView({ asked }: { asked: Asked }) {
	const groups = groupsOf(asked);
	const first = firstPlaces(groups);
	const byId = itemsById(groups);
	const { conflicts } = asked.answer;

	return (
		<div className={styles.answer}>
			{conflicts.length > 0 ? (
				<Conflicts conflicts={conflicts} byId={byId} />
			) : null}
			{groups.map((group) => (
				<GroupView key={group.key} group={group} first={first} depth={0} />
			))}
			<Raw answer={asked.answer} />
		</div>
	);
}

/** The pairs the owner marked as contradicting, each naming both items,
 * linking down to them, and which one is preferred and why, or that
 * neither is. */
function Conflicts({
	conflicts,
	byId,
}: {
	conflicts: readonly Conflict[];
	byId: Map<string, AnswerItem>;
}) {
	const text = (id: string) => byId.get(id)?.content ?? id;
	return (
		<Card title="The brain holds things that disagree" attention>
			<ul className={styles.conflicts}>
				{conflicts.map((conflict) => {
					const [one, other] = conflict.items;
					if (one === undefined || other === undefined) return null;
					return (
						<li key={conflict.items.join(":")} className={styles.conflict}>
							<p className={styles.pair}>
								<a className={styles.link} href={`#${anchorOf(one)}`}>
									{text(one)}
								</a>
								<span className={styles.against}>disagrees with</span>
								<a className={styles.link} href={`#${anchorOf(other)}`}>
									{text(other)}
								</a>
							</p>
							{conflict.prefer === null ? (
								<p className={styles.reason}>{REASONS[conflict.reason]}</p>
							) : (
								<p className={styles.reason}>
									<span className={styles.why}>{REASONS[conflict.reason]}</span>
									<span>Preferred: {text(conflict.prefer)}</span>
								</p>
							)}
						</li>
					);
				})}
			</ul>
		</Card>
	);
}

/** A group: its heading, the chain that led to it for a narrower topic,
 * its items or what is said when it has none, the topics related to it,
 * and the narrower ones under it. An answer of one list is named Answer. */
function GroupView({
	group,
	first,
	depth,
}: {
	group: Group;
	first: Map<string, string>;
	depth: 0 | 1;
}) {
	const heading = useId();
	const Heading = depth === 0 ? "h2" : "h3";
	const named = group.title === undefined ? undefined : heading;
	return (
		<section
			className={depth === 0 ? styles.group : styles.narrower}
			aria-labelledby={named}
			aria-label={named ? undefined : "Answer"}
		>
			{group.title === undefined ? null : (
				<Heading id={heading} className={styles.heading}>
					{group.title}
				</Heading>
			)}
			{group.through ? (
				<p className={styles.through}>Through {group.through.join(" › ")}</p>
			) : null}
			{group.items.length > 0 ? (
				<ol
					className={styles.items}
					aria-labelledby={named}
					aria-label={named ? undefined : "Answer"}
				>
					{group.items.map((item) => (
						<ItemCard
							key={item.id}
							item={item}
							anchor={first.get(item.id) === group.key}
						/>
					))}
				</ol>
			) : (
				<p className={styles.note}>{group.empty}</p>
			)}
			{group.related.length > 0 ? (
				<p className={styles.note}>Related: {group.related.join(", ")}</p>
			) : null}
			{group.narrower.map((under) => (
				<GroupView key={under.key} group={under} first={first} depth={1} />
			))}
		</section>
	);
}

/** An item as a card: its text and state, where it lives and came from and
 * what supports it, its subjects, and Why, the events behind it, closed
 * until opened. The first place an item is shown is where a conflict's
 * link lands. */
function ItemCard({ item, anchor }: { item: AnswerItem; anchor: boolean }) {
	const text = useId();
	const reasons = useId();
	const [open, setOpen] = useState(false);
	const state = stateOf(item);

	return (
		<li
			id={anchor ? anchorOf(item.id) : undefined}
			tabIndex={anchor ? -1 : undefined}
			className={styles.place}
		>
			<article aria-labelledby={text} className={styles.item}>
				<div className={styles.head}>
					<p id={text} className={styles.text}>
						{item.content}
					</p>
					<Status tone={state.tone}>{state.word}</Status>
				</div>
				<ul className={styles.facts} aria-label="Facts">
					<li>{kindWord(item.kind)}</li>
					<li>{storeWord(item.store)}</li>
					<li>{provenanceWord(item.provenance)}</li>
					<li>{supportLine(item)}</li>
				</ul>
				<SubjectBadges
					topics={item.subjects.topics}
					entities={item.subjects.entities}
				/>
				<div className={styles.actions}>
					<Button
						kind="ghost"
						aria-expanded={open}
						aria-controls={reasons}
						onClick={() => setOpen(!open)}
					>
						Why
					</Button>
				</div>
				<Timeline
					id={reasons}
					aria-label="Why"
					hidden={!open}
					className={styles.reasons}
				>
					{item.evidence.map((reason, index) => (
						<TimelineEntry
							// Events carry no id in an answer; their order is fixed.
							// biome-ignore lint/suspicious/noArrayIndexKey: see above
							key={index}
							time={writtenWhen(reason.occurred_at, reason.occurred_precision)}
							detail={
								<>
									<span className={styles.evidence}>{reason.text}</span>{" "}
									<span>{originWords(reason.origin)}</span>
								</>
							}
						>
							{reason.kind === "contradicts" ? "Against it: " : null}
							{verbWords(reason.verb, reason.origin)}
						</TimelineEntry>
					))}
				</Timeline>
			</article>
		</li>
	);
}

/** The response as a feature receives it, formatted, with Copy, closed
 * until opened. */
function Raw({ answer }: { answer: Asked["answer"] }) {
	const toast = useToast();
	const body = useId();
	const [open, setOpen] = useState(false);
	const json = JSON.stringify(answer, null, 2);

	function copy() {
		navigator.clipboard.writeText(json).then(
			() => toast.success("Answer copied"),
			() => toast.error("The answer could not be copied"),
		);
	}

	return (
		<div className={styles.raw}>
			<div className={styles.actions}>
				<Button
					aria-expanded={open}
					aria-controls={body}
					onClick={() => setOpen(!open)}
				>
					Show what a feature receives
				</Button>
			</div>
			<div id={body} hidden={!open} className={styles.rawBody}>
				<section
					// A region that scrolls is named and reachable by the keyboard, as
					// the package's Table is, so it can be scrolled without a pointer.
					// biome-ignore lint/a11y/noNoninteractiveTabindex: a scrolling region must be focusable to be scrolled from the keyboard.
					tabIndex={0}
					aria-label="What a feature receives"
					className={styles.json}
				>
					<pre className={styles.code}>{json}</pre>
				</section>
				<div className={styles.actions}>
					<Button onClick={copy}>Copy</Button>
				</div>
			</div>
		</div>
	);
}
